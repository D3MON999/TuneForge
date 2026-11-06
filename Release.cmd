@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-release.ps1"
echo.
pause
