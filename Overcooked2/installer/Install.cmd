@echo off
rem Double-click to install LobbyTools (installs BepInEx 5 x86 too if it's missing).
rem   Install.cmd -GameDir "D:\Games\Overcooked! 2"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
