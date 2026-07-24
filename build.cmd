@echo off
REM Hdiff package entry point.
REM   build.cmd       Build the framework-dependent (FDD) ZIP package.
REM   build.cmd fdd   Same as the default command.
setlocal
cd /d "%~dp0"

if "%~1"=="" goto :fdd
if /i "%~1"=="fdd" goto :fdd

echo Usage: build.cmd [fdd]
exit /b 1

:fdd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1"
exit /b %errorlevel%
