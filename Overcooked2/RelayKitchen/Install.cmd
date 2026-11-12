@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set "rc=%errorlevel%"
pause
exit /b %rc%
