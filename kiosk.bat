@echo off
setlocal
cd /d "%~dp0"
set "PY=python"
if exist "C:\Program Files\Python312\python.exe" set "PY=C:\Program Files\Python312\python.exe"
set "BROWSER_ARG="
if /I "%~1"=="--chrome" set "BROWSER_ARG=--chrome"
"%PY%" "%~dp0kiosk_start.py" %BROWSER_ARG%
if errorlevel 1 (
  pause
  exit /b 1
)
