@echo off
rem Builds LocalCoop.dll, installs it into the game's BepInEx\plugins\LocalCoop\ (when the game is closed)
rem and stages it in ..\dist\ for scripts\Release.ps1.
rem Game somewhere else?  build.cmd -p:GameDir="D:\Games\<game folder>"
rem Uses the repo's private .NET SDK in .tools\dotnet (git-ignored), else a dotnet on PATH.
set "DOTNET=%~dp0..\..\.tools\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
"%DOTNET%" build "%~dp0LocalCoop.csproj" -c Release -nologo %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
