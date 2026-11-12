@echo off
rem Starts Project Zomboid with MonitorCoop loaded (a monitor each for local split-screen).
rem Everything about how the game starts is read from the game's own ProjectZomboid64.json, so no
rem game file is modified; this window stays open while the game runs, and can be minimised.
rem   Play.cmd -debug      arguments are passed on to the game
rem Steam: set the game's launch options to
rem   "<this folder>\Play.cmd" %%command%%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Play.ps1" %*
if errorlevel 1 (
    echo.
    pause
)
