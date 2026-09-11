@echo off
REM FaceDiversityApp — launcher. Builds the engine, starts the local web UI, opens your browser.
setlocal
set PORT=8930
cd /d "%~dp0engine"

REM Stop a server still running from an earlier launch: it would keep the OLD engine alive (and lock the
REM build output) while serving the NEW page from disk -> "undefined" values, dead buttons. The server
REM listens through http.sys, so netstat reports the port as owned by PID 4 (System) - the process has to
REM be found by its command line, not by the port.
echo Stopping any previous FaceDiversityApp server...
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { $_.CommandLine -like '*facediv.dll*serve*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Host ('  stopped PID ' + $_.ProcessId) }"

echo Building FaceDiversityApp engine...
dotnet build --configuration Release -nologo
if errorlevel 1 (
  echo.
  echo BUILD FAILED — see errors above.
  pause
  exit /b 1
)

echo.
echo Starting the UI server in a separate window...
start "FaceDiversityApp UI  (close this window to stop the app)" dotnet "bin\Release\net8.0\facediv.dll" serve --port %PORT%

REM give the server a moment to bind before opening the browser
timeout /t 3 /nobreak >nul
start "" "http://localhost:%PORT%/"

echo.
echo  FaceDiversityApp is running:  http://localhost:%PORT%/
echo  The server runs in the other window — close it (or press Ctrl+C there) to stop.
echo.
endlocal
