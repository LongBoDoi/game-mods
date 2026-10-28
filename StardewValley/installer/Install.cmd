@echo off
rem Double-click to install Monitor Co-op (installs SMAPI too if it's missing).
rem   Install.cmd -GameDir "D:\Games\Stardew Valley"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
