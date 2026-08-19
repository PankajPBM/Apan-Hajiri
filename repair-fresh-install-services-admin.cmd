@echo off
setlocal

cd /d "%~dp0"
net session >nul 2>&1
if not "%errorlevel%"=="0" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0repair-fresh-install-services.ps1"

echo.
echo Press any key to close...
pause >nul
