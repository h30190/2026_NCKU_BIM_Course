@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install-lebr.ps1" -Uninstall
echo.
pause
