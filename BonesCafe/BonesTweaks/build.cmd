@echo off
rem Builds BonesTweaks.dll, installs it into the game's BepInEx\plugins\BonesTweaks\ (when the game is closed)
rem and stages it in ..\dist\ for scripts\Release.ps1.
rem Game somewhere else?  build.cmd -p:GameDir="D:\Games\<game folder>"
dotnet build "%~dp0BonesTweaks.csproj" -c Release -nologo %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
