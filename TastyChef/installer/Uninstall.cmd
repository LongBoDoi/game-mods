@echo off
rem Removes Tasty Chef Local Co-op and lets the game run as a single copy again.
rem BepInEx stays installed; delete winhttp.dll in the game folder to disable it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
