@echo off
setlocal
set "levelPython=%~dp0..\..\.tools\oc2-python\python.exe"
if not exist "%levelPython%" set "levelPython=python"
"%levelPython%" "%~dp0build_level.py" %*
exit /b %errorlevel%
