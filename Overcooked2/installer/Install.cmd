@echo off
rem Double-click and it stays open: pick what to install, it does it, then asks again with the
rem list refreshed. Choose Q to close.
rem   LobbyTools       F6 lobby panel
rem   QuickKeyRestart  F5 restarts the level
rem   OC2DIYLevel      third-party level pack - NOT included, you supply the extracted folder
rem   BepInEx          the loader itself, newest stable 5.x by default
rem
rem   Install.cmd                                 menu, until you choose Q
rem   Install.cmd -All                            everything, once, no questions
rem   Install.cmd -Only QuickKeyRestart           just that mod, once
rem   Install.cmd -Only LobbyTools,BepInEx        several at once
rem   Install.cmd -Only BepInEx -BepInExVersion 5.4.23.2    pin a BepInEx version
rem   Install.cmd -Only OC2DIYLevel -DiyPackDir "C:\path	o\New Pack OC2"
rem   Install.cmd -GameDir "D:\Games\Overcooked! 2"        use a specific game folder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set "rc=%errorlevel%"
rem The menu holds the window open by itself, so only wait for a key when there is something to
rem read that would otherwise vanish: a failure, or a one-shot run started with flags.
if not "%rc%"=="0" (
    echo.
    pause
) else if not "%~1"=="" (
    echo.
    pause
)
exit /b %rc%
