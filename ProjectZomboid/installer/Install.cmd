@echo off
rem Double-click to install Monitor Co-op: a monitor each for local split-screen players.
rem It only copies the mod and its launcher into <game>\MonitorCoop\ - no game file is changed.
rem   Install.cmd -Shortcut                            also puts a shortcut on the desktop
rem   Install.cmd -GameDir "D:\Games\ProjectZomboid"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
