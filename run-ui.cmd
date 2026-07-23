@echo off
REM Hdiff UI development launcher.
REM Always build and run the WinForms project from the current source tree.
setlocal
cd /d "%~dp0"

dotnet run --project "src\Hdiff.UI\Hdiff.UI.csproj"
exit /b %errorlevel%
