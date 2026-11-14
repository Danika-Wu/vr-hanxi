[CmdletBinding()]
param(
    [string]$RepoPath = (Split-Path -Parent $PSScriptRoot),
    [switch]$AllowPullWhileUnityOpen,
    [switch]$SkipSettleCheck
)

$ErrorActionPreference = 'Stop'
$RepoPath = [System.IO.Path]::GetFullPath($RepoPath)
$stateDir = Join-Path $RepoPath '.github-sync'
$logPath = Join-Path $stateDir 'sync.log'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

$gitCandidates = @(
    (Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe'),
    (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\native\git\cmd\git.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$GitExe = $gitCandidates | Select-Object -First 1
if (-not $GitExe) {
    throw 'git.exe was not found. Install Git for Windows or update GitHubAutoSync.ps1 with its path.'
}

function Write-SyncLog {
    param([string]$Message)
    $line = '{0:u} {1}' -f (Get-Date), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    # Git writes normal progress to stderr. Windows PowerShell can promote that
    # stream to a terminating ErrorRecord when the script preference is Stop,
    # so judge success only by Git's process exit code.
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $GitExe -C $RepoPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }
    $text = ($output | Out-String).Trim()
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed ($exitCode): $text"
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $text
    }
}

function Invoke-GitNetwork {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [ValidateRange(1, 5)][int]$Attempts = 3,
        [switch]$AllowFailure
    )

    $lastResult = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $lastResult = Invoke-Git -Arguments $Arguments -AllowFailure
        if ($lastResult.ExitCode -eq 0) {
            return $lastResult
        }

        $transient = $lastResult.Output -match 'unable to access|Could not connect|Connection reset|TLS|SSL|timed out|remote end hung up|RPC failed'
        if (-not $transient -or $attempt -eq $Attempts) {
            break
        }

        Write-SyncLog "Network attempt $attempt/$Attempts failed; retrying."
        Start-Sleep -Seconds (5 * $attempt)
    }

    if (-not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed after $Attempts attempt(s): $($lastResult.Output)"
    }
    return $lastResult
}

function Test-UnityOpenForProject {
    try {
        $escapedPath = [Regex]::Escape($RepoPath.TrimEnd('\'))
        $unityProcesses = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction Stop
        foreach ($process in $unityProcesses) {
            if ($process.CommandLine -match $escapedPath) {
                return $true
            }
        }
        return $false
    }
    catch {
        return [bool](Get-Process -Name Unity -ErrorAction SilentlyContinue)
    }
}

function Get-WorkingState {
    (Invoke-Git -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output
}

$sha = [System.Security.Cryptography.SHA256]::Create()
$pathBytes = [Text.Encoding]::UTF8.GetBytes($RepoPath.ToLowerInvariant())
$mutexId = ([BitConverter]::ToString($sha.ComputeHash($pathBytes))).Replace('-', '').Substring(0, 20)
$mutex = New-Object Threading.Mutex($false, "Global\GitHubAutoSync_$mutexId")

if (-not $mutex.WaitOne(0)) {
    exit 0
}

try {
    if (-not (Test-Path -LiteralPath (Join-Path $RepoPath '.git'))) {
        Write-SyncLog 'Skipped: repository is not initialized.'
        exit 2
    }

    $remote = Invoke-Git -Arguments @('remote', 'get-url', 'origin') -AllowFailure
    if ($remote.ExitCode -ne 0) {
        Write-SyncLog 'Skipped: origin remote is not configured.'
        exit 2
    }

    $unfinished = @(
        (Join-Path $RepoPath '.git\MERGE_HEAD'),
        (Join-Path $RepoPath '.git\rebase-merge'),
        (Join-Path $RepoPath '.git\rebase-apply')
    ) | Where-Object { Test-Path -LiteralPath $_ }
    if ($unfinished.Count -gt 0) {
        Write-SyncLog 'Paused: an unfinished merge or rebase needs manual attention.'
        exit 3
    }

    $conflicts = Invoke-Git -Arguments @('diff', '--name-only', '--diff-filter=U')
    if ($conflicts.Output) {
        Write-SyncLog "Paused: unresolved conflicts: $($conflicts.Output -replace "`r?`n", ', ')"
        exit 3
    }

    $branch = (Invoke-Git -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')).Output
    if (-not $branch -or $branch -eq 'HEAD') {
        Write-SyncLog 'Paused: detached HEAD.'
        exit 3
    }

    $before = Get-WorkingState
    if ($before -and -not $SkipSettleCheck) {
        Start-Sleep -Seconds 10
        $after = Get-WorkingState
        if ($before -ne $after) {
            Write-SyncLog 'Deferred: files are still changing; will retry next interval.'
            exit 0
        }
    }

    Invoke-GitNetwork -Arguments @('fetch', '--prune', 'origin') | Out-Null

    if (Get-WorkingState) {
        Invoke-Git -Arguments @('add', '--all') | Out-Null
        $staged = Invoke-Git -Arguments @('diff', '--cached', '--quiet') -AllowFailure
        if ($staged.ExitCode -eq 1) {
            $machine = $env:COMPUTERNAME
            $message = 'Auto-sync {0:yyyy-MM-dd HH:mm:ss} from {1}' -f (Get-Date), $machine
            Invoke-Git -Arguments @('commit', '-m', $message) | Out-Null
            Write-SyncLog "Committed: $message"
        }
    }

    $remoteRef = "refs/remotes/origin/$branch"
    $remoteExists = (Invoke-Git -Arguments @('show-ref', '--verify', '--quiet', $remoteRef) -AllowFailure).ExitCode -eq 0
    if ($remoteExists) {
        $counts = (Invoke-Git -Arguments @('rev-list', '--left-right', '--count', "HEAD...origin/$branch")).Output -split '\s+'
        $behind = [int]$counts[1]
        if ($behind -gt 0) {
            $unityOpen = Test-UnityOpenForProject
            if ($unityOpen -and -not $AllowPullWhileUnityOpen) {
                Write-SyncLog "Incoming changes detected ($behind commit(s)); pull deferred until Unity is closed."
                exit 0
            }

            $pull = Invoke-Git -Arguments @('pull', '--rebase', 'origin', $branch) -AllowFailure
            if ($pull.ExitCode -ne 0) {
                Invoke-Git -Arguments @('rebase', '--abort') -AllowFailure | Out-Null
                Write-SyncLog "Paused: rebase conflict or pull failure. $($pull.Output)"
                exit 3
            }
            Write-SyncLog "Pulled $behind incoming commit(s)."
        }
    }

    $push = Invoke-GitNetwork -Arguments @('push', '--set-upstream', 'origin', $branch) -AllowFailure
    if ($push.ExitCode -ne 0) {
        Write-SyncLog "Push failed: $($push.Output)"
        exit 4
    }

    Write-SyncLog 'Sync completed.'
    exit 0
}
catch {
    Write-SyncLog "Error: $($_.Exception.Message)"
    exit 5
}
finally {
    $mutex.ReleaseMutex() | Out-Null
    $mutex.Dispose()
}
