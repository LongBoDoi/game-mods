@echo off
rem Removes LobbyTools. BepInEx, your settings and your ban list stay; delete winhttp.dll in the
rem game folder to disable all mods.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
