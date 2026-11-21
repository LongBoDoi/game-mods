@echo off
rem Builds OC2Tweaks.dll and stages it in ..\dist\ for scripts\Release.ps1.
rem   build.cmd -p:DeployToGame=true           also installs it into the game (game closed)
rem   build.cmd -p:GameDir="D:\Games\Overcooked! 2"   game somewhere else
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
"%DOTNET%" build "%~dp0OC2Tweaks.csproj" -c Release -nologo %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
