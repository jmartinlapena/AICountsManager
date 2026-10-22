@echo off
setlocal
pwsh.exe -NoProfile -File "%~dp0build_release.ps1" %*
exit /b %errorlevel%
