[CmdletBinding()]
param(
    [ValidateRange(1, 120)][int]$IntervalMinutes = 5,
    [string]$RepoPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$RepoPath = [System.IO.Path]::GetFullPath($RepoPath)
$syncScript = Join-Path $RepoPath 'Tools\GitHubAutoSync.ps1'
if (-not (Test-Path -LiteralPath $syncScript)) {
    throw "Sync script not found: $syncScript"
}

$gitCandidates = @(
    (Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe'),
    (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\native\git\cmd\git.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$gitExe = $gitCandidates | Select-Object -First 1
if (-not $gitExe) {
    throw 'git.exe was not found. Install Git for Windows before enabling auto-sync.'
}

$origin = (& $gitExe -C $RepoPath remote get-url origin 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $origin) {
    $lockKey = 'lfs.{0}/info/lfs.locksverify' -f $origin.TrimEnd('/')
    & $gitExe -C $RepoPath config --local $lockKey false
}

$repoName = Split-Path -Leaf $RepoPath
$taskName = "GitHub Auto Sync - $repoName"
$arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$syncScript`" -RepoPath `"$RepoPath`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $RepoPath
$trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1)) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) `
    -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal -Force | Out-Null

Write-Output "Installed scheduled task: $taskName"
Write-Output "Interval: every $IntervalMinutes minute(s) while this user is logged in."
Write-Output "Log: $(Join-Path $RepoPath '.github-sync\sync.log')"
