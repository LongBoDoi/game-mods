@echo off
rem Build and stage RandomLevel.dll. Install with installer\Install.cmd -Only RandomLevel.
rem Uses dotnet from PATH, else %USERPROFILE%\.dotnet, else the repo's .tools\dotnet.
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if not errorlevel 1 goto build
set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if exist "%DOTNET%" goto build
set "DOTNET=%~dp0..\..\.tools\dotnet\dotnet.exe"
:build
"%DOTNET%" build "%~dp0RandomLevel.csproj" -c Release -nologo %*
if errorlevel 1 exit /b 1
