@echo off
setlocal
set "SCRIPT=%~dp0Install_Kiosk.ps1"

if not exist "%SCRIPT%" (
  echo FEHLER: "%SCRIPT%" wurde nicht gefunden.
  pause
  exit /b 1
)

rem Nur dieser PowerShell-Prozess erhaelt Bypass. Das Skript fordert die
rem Administratorrechte selbst per UAC an.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"

if errorlevel 1 (
  echo.
  echo Installation fehlgeschlagen oder UAC wurde abgebrochen.
  echo Details: "%~dp0Install_Kiosk.log"
  pause
  exit /b 1
)

echo.
echo Installation abgeschlossen.
pause
