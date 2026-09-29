@echo off
rem Removes Hold Act. BepInEx stays installed; delete winhttp.dll in the game folder to disable it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
