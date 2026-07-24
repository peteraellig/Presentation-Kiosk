@echo off
setlocal
set "KIOSK_DIR=%~dp0"
set "PYTHON=%KIOSK_DIR%.venv\Scripts\python.exe"
set "FIREFOX=C:\Program Files\Mozilla Firefox\firefox.exe"

if not exist "%PYTHON%" (
  echo Python-Umgebung fehlt. Bitte Installer\Install_Kiosk.cmd ausfuehren.
  pause
  exit /b 1
)

where wt.exe >nul 2>&1
if errorlevel 1 (
  start "Kiosk Server" cmd.exe /k ""%PYTHON%" "%KIOSK_DIR%app.py""
  timeout /t 2 /nobreak >nul
  start "StreamDeck" cmd.exe /k ""%PYTHON%" "%KIOSK_DIR%app_streamdeck.py""
) else (
  start "" wt.exe -w Kiosk new-tab --title "Kiosk Server" cmd.exe /k ""%PYTHON%" "%KIOSK_DIR%app.py""
  timeout /t 2 /nobreak >nul
  start "" wt.exe -w Kiosk new-tab --title "StreamDeck" cmd.exe /k ""%PYTHON%" "%KIOSK_DIR%app_streamdeck.py""
)
timeout /t 5 /nobreak >nul
taskkill /IM firefox.exe /F /T >nul 2>&1
for /L %%I in (1,1,10) do (
  tasklist /FI "IMAGENAME eq firefox.exe" 2>nul | find /I "firefox.exe" >nul || goto firefox_stopped
  timeout /t 1 /nobreak >nul
)
:firefox_stopped
start "" "%FIREFOX%" -kiosk -private-window "http://localhost:53100/"
