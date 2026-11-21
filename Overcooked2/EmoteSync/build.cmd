@echo off
rem Builds EmoteSync.dll with the C# compiler that ships with Windows (no .NET SDK needed) and
rem stages it in ..\dist\. It compiles against the game's own mscorlib/System (the .NET 3.5
rem profile Overcooked! 2 runs on), so it can't accidentally use APIs the game doesn't have.
rem   build.cmd            build and stage in ..\dist\
rem   build.cmd deploy     also copy into the game's BepInEx\plugins\EmoteSync (game closed)
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

echo Building EmoteSync.dll...
"%CSC%" /nologo /noconfig /nostdlib+ /target:library /optimize+ /langversion:5 ^
  /out:"%OUT%\EmoteSync.dll" ^
  /r:"%MANAGED%\mscorlib.dll" ^
  /r:"%MANAGED%\System.dll" ^
  /r:"%MANAGED%\System.Core.dll" ^
  /r:"%MANAGED%\Assembly-CSharp.dll" ^
  /r:"%MANAGED%\Assembly-CSharp-firstpass.dll" ^
  /r:"%MANAGED%\UnityEngine.dll" ^
  /r:"%MANAGED%\UnityEngine.CoreModule.dll" ^
  /r:"%MANAGED%\UnityEngine.UI.dll" ^
  /r:"%MANAGED%\UnityEngine.UIModule.dll" ^
  /r:"%MANAGED%\UnityEngine.TextRenderingModule.dll" ^
  /r:"%MANAGED%\UnityEngine.InputModule.dll" ^
  /r:"%MANAGED%\UnityEngine.IMGUIModule.dll" ^
  /r:"%MANAGED%\UnityEngine.AnimationModule.dll" ^
  /r:"%MANAGED%\UnityEngine.AudioModule.dll" ^
  /r:"%MANAGED%\UnityEngine.PhysicsModule.dll" ^
  /r:"%CORE%\BepInEx.dll" ^
  /r:"%CORE%\0Harmony.dll" ^
  "%~dp0*.cs"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

if not exist "%~dp0..\dist" mkdir "%~dp0..\dist"
copy /y "%OUT%\EmoteSync.dll" "%~dp0..\dist\EmoteSync.dll" >nul
echo Built: %OUT%\EmoteSync.dll  (staged in dist\)

if /i "%~1"=="deploy" (
    if not exist "%GAME_DIR%\BepInEx\plugins\EmoteSync" mkdir "%GAME_DIR%\BepInEx\plugins\EmoteSync"
    copy /y "%OUT%\EmoteSync.dll" "%GAME_DIR%\BepInEx\plugins\EmoteSync\EmoteSync.dll" >nul
    if errorlevel 1 (
        echo Could not copy into the game. Close Overcooked! 2 and try again.
        exit /b 1
    )
    echo Installed into "%GAME_DIR%\BepInEx\plugins\EmoteSync".
)
endlocal
