@echo off
rem Removes Bone's Cafe Tweaks. BepInEx stays installed; delete winhttp.dll in the game folder to disable it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
