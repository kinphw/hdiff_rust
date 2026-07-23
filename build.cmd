@echo off
REM Hdiff package entry point.
REM   build.cmd       Build a self-contained ZIP package (recommended for transfer).
REM   build.cmd zip   Same as the default command.
REM   build.cmd fdd   Build a framework-dependent ZIP package.
setlocal
cd /d "%~dp0"

if "%~1"=="" goto :zip
if /i "%~1"=="zip" goto :zip
if /i "%~1"=="fdd" goto :fdd

echo Usage: build.cmd [zip^|fdd]
exit /b 1

:zip
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1"
exit /b %errorlevel%

:fdd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1" -FrameworkDependent
exit /b %errorlevel%
