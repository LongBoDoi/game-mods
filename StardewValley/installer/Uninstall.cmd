@echo off
rem Removes Monitor Co-op. SMAPI, your other mods and this mod's config.json stay; local co-op goes
rem back to the game's own split-screen in one window.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
echo.
pause
