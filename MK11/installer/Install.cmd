@echo off
rem Double-click to install MK11 Tweaks for Mortal Kombat 11 (with the ASI loader if it's missing).
rem   Install.cmd -GameDir "D:\Games\Mortal Kombat 11"   uses a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
