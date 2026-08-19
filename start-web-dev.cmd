@echo off
setlocal

set "ROOT=%~dp0"
set "BACKEND=%ROOT%E.Attendance.Web"
set "FRONTEND=%ROOT%e-attendance-next"
set "WEB_PORT=8073"

echo Starting E-Attendance web stack...
echo Backend:  %BACKEND%
echo Frontend: %FRONTEND%
echo Web Port: %WEB_PORT%
echo.

echo Checking for process using port %WEB_PORT%...
for /f "tokens=5" %%P in ('netstat -ano ^| findstr /R /C:":%WEB_PORT% .*LISTENING"') do (
  echo Stopping PID %%P on port %WEB_PORT%...
  taskkill /PID %%P /F >nul 2>&1
)

start "E-Attendance Backend (.NET API :8081)" cmd /k "cd /d ""%BACKEND%"" && dotnet watch run"
start "E-Attendance Frontend (Next.js :%WEB_PORT%)" cmd /k "cd /d ""%FRONTEND%"" && if exist "".next\dev\lock"" del /f /q "".next\dev\lock"" && if not exist ""node_modules"" (npm install) else (echo node_modules exists, skip npm install) && npm run dev -- --hostname 127.0.0.1 --port %WEB_PORT%"

echo Both windows launched.
echo - API expected at http://localhost:8081
echo - Website expected at http://localhost:%WEB_PORT%

endlocal
