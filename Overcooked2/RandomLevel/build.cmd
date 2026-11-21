@echo off
rem Build and stage RandomLevel.dll. Install with installer\Install.cmd -Only RandomLevel.
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
"%DOTNET%" build "%~dp0RandomLevel.csproj" -c Release -nologo %*
if errorlevel 1 exit /b 1
