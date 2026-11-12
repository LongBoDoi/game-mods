@echo off
rem Builds MonitorCoop.jar (the Java agent, compiled against the game's own projectzomboid.jar) and
rem stages it in ..\dist\MonitorCoop\ for scripts\Release.ps1.
rem   build.cmd install                              also installs it into the game
rem   build.cmd clean                                rebuilds everything
rem   build.cmd -GameDir "D:\Games\ProjectZomboid"   game somewhere else
setlocal
set "HERE=%~dp0"
set "ARGS="
:args
if "%~1"=="" goto run
if /i "%~1"=="install" (set "ARGS=%ARGS% -Install") else (
if /i "%~1"=="clean" (set "ARGS=%ARGS% -Clean") else (
set "ARGS=%ARGS% %1"))
shift
goto args
:run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%build.ps1" %ARGS%
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
