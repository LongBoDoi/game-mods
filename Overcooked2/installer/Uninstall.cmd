@echo off
rem Double-click and it stays open: pick what to remove, it does it, then asks again with only
rem what is still installed. Choose Q to close. Removing a mod leaves its settings behind.
rem Removing the OC2DIYLevel pack takes back only the entries it installed, by its manifest.
rem Removing BepInEx disables every mod in the game; it asks first, and keeps other mods' files
rem and all saved settings unless you pass -PurgeBepInEx.
rem
rem   Uninstall.cmd                               menu, until you choose Q
rem   Uninstall.cmd -Only QuickKeyRestart         just that mod, once
rem   Uninstall.cmd -Only OC2DIYLevel             just the level pack
rem   Uninstall.cmd -All                          everything, once
rem   Uninstall.cmd -Only BepInEx -PurgeBepInEx   also delete every plugin and setting
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
set "rc=%errorlevel%"
rem Same as Install.cmd: the menu waits for you, so only hold the window on a failure or a
rem one-shot run started with flags.
if not "%rc%"=="0" (
    echo.
    pause
) else if not "%~1"=="" (
    echo.
    pause
)
exit /b %rc%
