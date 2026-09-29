@echo off
rem Double-click to install Hold Act for Bone's Cafe (installs BepInEx 5 too if it's missing).
rem   Install.cmd -GameDir "D:\Games\Bone's Cafe"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
