@echo off
rem Runs the offline checks: the layout maths, and every patch against the installed game jar.
rem   run.cmd -NoGame                               layout maths only
rem   run.cmd -GameDir "D:\Games\ProjectZomboid"    game somewhere else
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Check.ps1" %*
if errorlevel 1 (
    echo.
    echo Checks FAILED.
    exit /b 1
)
