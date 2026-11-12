@echo off
rem Removes Monitor Co-op: the <game>\MonitorCoop folder and the desktop shortcut, if there is one.
rem Your settings in <user>\Zomboid\MonitorCoop.ini stay. No game file was ever changed, so local
rem split-screen is the game's own again as soon as the mod is gone.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
