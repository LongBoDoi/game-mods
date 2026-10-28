@echo off
rem Builds MonitorCoop.dll and stages it with its manifest in ..\dist\MonitorCoop\ for scripts\Release.ps1.
rem   build.cmd -p:DeployToGame=true                     also installs it into the game's Mods folder
rem   build.cmd -p:GameDir="D:\Games\Stardew Valley"     game somewhere else
dotnet build "%~dp0MonitorCoop.csproj" -c Release -nologo %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
