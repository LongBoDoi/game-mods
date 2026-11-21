@echo off
rem Builds ModUpdater.dll with the C# compiler that ships with Windows (no .NET SDK needed) and
rem stages it, with its helper ModUpdater.ps1, in ..\dist\. It compiles against the game's own
rem mscorlib/System (the .NET 3.5 profile Overcooked! 2 runs on).
rem   build.cmd            build and stage in ..\dist\
rem   build.cmd deploy     also copy into the game's BepInEx\plugins\ModUpdater (game closed)
rem   set GAME_DIR=D:\Games\Overcooked! 2   before running, if the game is somewhere else
setlocal
if "%GAME_DIR%"=="" set "GAME_DIR=E:\Steam\steamapps\common\Overcooked! 2"
set "MANAGED=%GAME_DIR%\Overcooked2_Data\Managed"
set "CORE=%GAME_DIR%\BepInEx\core"
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "OUT=%~dp0bin\Release"

if not exist "%MANAGED%\Assembly-CSharp.dll" (
    echo Overcooked! 2 not found at "%GAME_DIR%". Set GAME_DIR to your game folder.
    exit /b 1
)
if not exist "%CORE%\BepInEx.dll" (
    echo BepInEx 5 is not installed in the game folder. Run installer\Install.cmd first.
    exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"

echo Building ModUpdater.dll...
"%CSC%" /nologo /noconfig /nostdlib+ /target:library /optimize+ /langversion:5 ^
  /out:"%OUT%\ModUpdater.dll" ^
  /r:"%MANAGED%\mscorlib.dll" ^
  /r:"%MANAGED%\System.dll" ^
  /r:"%MANAGED%\System.Core.dll" ^
  /r:"%MANAGED%\Assembly-CSharp.dll" ^
  /r:"%MANAGED%\Assembly-CSharp-firstpass.dll" ^
  /r:"%MANAGED%\UnityEngine.dll" ^
  /r:"%MANAGED%\UnityEngine.CoreModule.dll" ^
  /r:"%CORE%\BepInEx.dll" ^
  /r:"%CORE%\Mono.Cecil.dll" ^
  "%~dp0*.cs"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

if not exist "%~dp0..\dist" mkdir "%~dp0..\dist"
copy /y "%OUT%\ModUpdater.dll" "%~dp0..\dist\ModUpdater.dll" >nul
copy /y "%~dp0ModUpdater.ps1" "%~dp0..\dist\ModUpdater.ps1" >nul
echo Built: %OUT%\ModUpdater.dll  (staged in dist\ with ModUpdater.ps1)

if /i "%~1"=="deploy" (
    if not exist "%GAME_DIR%\BepInEx\plugins\ModUpdater" mkdir "%GAME_DIR%\BepInEx\plugins\ModUpdater"
    copy /y "%OUT%\ModUpdater.dll" "%GAME_DIR%\BepInEx\plugins\ModUpdater\ModUpdater.dll" >nul
    if errorlevel 1 (
        echo Could not copy into the game. Close Overcooked! 2 and try again.
        exit /b 1
    )
    copy /y "%~dp0ModUpdater.ps1" "%GAME_DIR%\BepInEx\plugins\ModUpdater\ModUpdater.ps1" >nul
    echo Installed into "%GAME_DIR%\BepInEx\plugins\ModUpdater".
)
endlocal
