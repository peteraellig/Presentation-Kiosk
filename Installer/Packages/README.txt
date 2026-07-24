Dieser Ordner macht den Kiosk-Installer bei Bedarf offline-fähig.

Erwartete Dateien:
  python-3.12.6-amd64.exe
  FirefoxSetup.exe
  VC_redist.x64.exe
  hidapi-win.zip

Python-Wheels kommen in:
  Packages\wheels\

Wheels auf einem Online-PC vorbereiten:
  py -3.12 -m pip download -r ..\requirements.txt -d .\wheels

Online-Betrieb:
  Install_Kiosk.cmd lädt fehlende Dateien automatisch in diesen Ordner.

Erzwungener Offline-Test:
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ..\Install_Kiosk.ps1 -Offline
