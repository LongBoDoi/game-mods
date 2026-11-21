@echo off
rem Build and stage RandomLevel.dll. Install with installer\Install.cmd -Only RandomLevel.
dotnet build "%~dp0RandomLevel.csproj" -c Release -nologo %*
if errorlevel 1 exit /b 1
