@echo off
REM Removes the Kebab Chefs mods but keeps your settings files.
REM Add -RemoveConfig on the command line if you want those gone too.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*

echo.
pause
