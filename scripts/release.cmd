@echo off
rem Builds a game's mods and zips them with its installer into .release\.
rem   release.cmd <Game> <version>             e.g.  release.cmd MK11 1.0.1
rem   release.cmd <Game> <version> -Publish    also creates the GitHub release <Game>-v<version>
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Release.ps1" %*
exit /b %errorlevel%
