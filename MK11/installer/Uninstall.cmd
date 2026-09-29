@echo off
rem Removes MK11 Tweaks but keeps MK11Tweaks.ini. Add -RemoveConfig to delete it too.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
