@echo off
setlocal
if "%GAME_DIR%"=="" set "GAME_DIR=E:\Steam\steamapps\common\Overcooked! 2"
set "levelPython=%~dp0..\..\.tools\oc2-python\python.exe"
if not exist "%levelPython%" set "levelPython=python"
"%levelPython%" "%~dp0build_level.py" --source "%GAME_DIR%\BepInEx\plugins\OC2DIYLevel" %*
exit /b %errorlevel%
