@echo off
rem Double-click to install LemonCoop for Lemon Cake (installs UE4SS too if it's missing).
rem   Install.cmd -GameDir "D:\Games\Lemon Cake"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
