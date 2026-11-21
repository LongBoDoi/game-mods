@echo off
rem Builds LocalCoop.dll, installs it into the game's BepInEx\plugins\LocalCoop\ (when the game is closed)
rem and stages it in ..\dist\ for scripts\Release.ps1.
rem Game somewhere else?  build.cmd -p:GameDir="D:\Games\<game folder>"
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
"%DOTNET%" build "%~dp0LocalCoop.csproj" -c Release -nologo %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
