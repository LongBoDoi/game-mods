@echo off
rem Packs the gamepad-bindings pak and stages LemonCoop in ..\dist\ for scripts\Release.ps1.
rem   build.cmd install    also installs the result into the game (UE4SS included if missing)
set "ARGS="
if /i "%~1"=="install" set "ARGS=-Install"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %ARGS%
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
