@echo off
rem Builds AutoFishCoop.dll and stages it with its manifest in ..\dist\AutoFishCoop\ for scripts\Release.ps1.
rem   build.cmd -p:DeployToGame=true                     also installs it into the game's Mods folder
rem   build.cmd -p:GameDir="D:\Games\Stardew Valley"     game somewhere else
dotnet build "%~dp0AutoFishCoop.csproj" -c Release -nologo %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
