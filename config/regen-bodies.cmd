@echo off
REM Regenerate the "SexPlague Bodies" OBody PDA config from Bodies of Tamriel + sexplague_bodies.yaml.
REM Portable: the engine reads the BOT presets path, game version, and output location from
REM config\settings.json (edit that per PC). Double-click this file (runs in cmd.exe — no PowerShell).
setlocal
for %%I in ("%~dp0..") do set "APP=%%~fI"
set "DLL=%APP%\engine\bin\Release\net8.0\facediv.dll"
if not exist "%DLL%" (
  echo Engine not built yet. Run run-ui.cmd once ^(it builds the engine^), then retry.
  pause
  exit /b 1
)

dotnet "%DLL%" sexplague-bodies

echo.
echo Done. Reinstall / refresh the "SexPlague Bodies" mod in your mod manager if the pools changed.
pause
endlocal
