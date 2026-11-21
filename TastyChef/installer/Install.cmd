@echo off
rem Double-click to install Tasty Chef Local Co-op (installs BepInEx 5 too if it's missing or too old).
rem   Install.cmd -GameDir "D:\Games\Tasty Chef"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
