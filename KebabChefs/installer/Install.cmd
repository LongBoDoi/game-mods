@echo off
REM Double-click wrapper. Bypasses the execution policy for this one script only,
REM which avoids the usual "running scripts is disabled on this system" refusal
REM without changing any machine-wide setting.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*

echo.
pause
