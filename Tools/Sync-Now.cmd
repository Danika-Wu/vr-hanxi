@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GitHubAutoSync.ps1" -RepoPath "%~dp0.." -SkipSettleCheck
set result=%ERRORLEVEL%
if not "%result%"=="0" (
  echo.
  echo Sync needs attention. See .github-sync\sync.log
)
exit /b %result%

