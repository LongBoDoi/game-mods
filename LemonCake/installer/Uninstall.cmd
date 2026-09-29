@echo off
rem Removes LemonCoop and its gamepad-bindings pak. UE4SS stays installed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
