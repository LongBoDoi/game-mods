@echo off
rem Double-click to install Hold Act for Bone's Cafe.
rem   Install.cmd -Uninstall              removes the mod
rem   Install.cmd -GameDir "D:\Games\Bone's Cafe"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-HoldAct.ps1" %*
echo.
pause
