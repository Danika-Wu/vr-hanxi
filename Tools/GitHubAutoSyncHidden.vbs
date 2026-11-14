Option Explicit

Dim fileSystem, shell, toolsDirectory, repoPath, syncScript, command, exitCode

Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function

Set fileSystem = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

toolsDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
repoPath = fileSystem.GetParentFolderName(toolsDirectory)
syncScript = fileSystem.BuildPath(toolsDirectory, "GitHubAutoSync.ps1")

command = "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File " _
    & Quote(syncScript) & " -RepoPath " & Quote(repoPath)

' Window style 0 is completely hidden. Wait for completion so Task Scheduler
' still records the real exit code and respects the IgnoreNew policy.
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode

