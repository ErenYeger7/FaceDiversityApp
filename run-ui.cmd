@echo off
REM FaceDiversityApp — launcher. Builds the engine, starts the local web UI, opens your browser.
setlocal
set PORT=8930
cd /d "%~dp0engine"

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
