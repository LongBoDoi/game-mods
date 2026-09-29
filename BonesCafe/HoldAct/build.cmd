@echo off
rem Builds HoldAct.dll, installs it into the game's BepInEx\plugins\HoldAct\ (when the game is closed)
rem and stages it in ..\dist\ for scripts\Release.ps1.
rem Game somewhere else?  build.cmd -p:GameDir="D:\Games\<game folder>"
dotnet build "%~dp0HoldAct.csproj" -c Release -nologo %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
