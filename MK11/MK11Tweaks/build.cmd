@echo off
rem Builds MK11Tweaks.asi (llvm-mingw, fetched on first run) and stages it with the ASI loader
rem in ..\dist\ for scripts\Release.ps1.
rem   build.cmd install    also installs it into the game's Binaries\Retail
rem   build.cmd clean      rebuilds everything
setlocal
set "HERE=%~dp0"
set "ARGS="
:args
if "%~1"=="" goto run
if /i "%~1"=="install" set "ARGS=%ARGS% -Install"
if /i "%~1"=="clean" set "ARGS=%ARGS% -Clean"
shift
goto args
:run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%build.ps1" %ARGS%
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
