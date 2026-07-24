#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Offline
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$KioskDir = Split-Path -Parent $InstallerDir
$PackagesDir = Join-Path $InstallerDir "Packages"
$RequirementsFile = Join-Path $InstallerDir "requirements.txt"
$PythonVersion = "3.12.6"
$PythonInstallerName = "python-$PythonVersion-amd64.exe"
$PythonUrl = "https://www.python.org/ftp/python/$PythonVersion/$PythonInstallerName"
$FirefoxInstallerName = "FirefoxSetup.exe"
$FirefoxUrl = "https://download.mozilla.org/?product=firefox-latest&os=win64&lang=de"
$VCRedistName = "VC_redist.x64.exe"
$VCRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
$HidapiZipName = "hidapi-win.zip"
$HidapiUrl = "https://github.com/libusb/hidapi/releases/download/hidapi-0.15.0/hidapi-win.zip"
$VenvDir = Join-Path $KioskDir ".venv"
$VenvPython = Join-Path $VenvDir "Scripts\python.exe"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $elevatedArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$($MyInvocation.MyCommand.Path)`""
    )
    if ($Offline) { $elevatedArguments += "-Offline" }
    $elevated = Start-Process -FilePath "powershell.exe" -Verb RunAs `
        -ArgumentList $elevatedArguments -Wait -PassThru
    exit $elevated.ExitCode
}

$LogFile = Join-Path $InstallerDir "Install_Kiosk.log"
try {
    Start-Transcript -LiteralPath $LogFile -Force | Out-Null
} catch {
    Write-Warning "Protokoll konnte nicht gestartet werden: $($_.Exception.Message)"
}

trap {
    $message = $_.Exception.Message
    Write-Host ""
    Write-Host "INSTALLATION FEHLGESCHLAGEN" -ForegroundColor Red
    Write-Host $message -ForegroundColor Red
    Write-Host "Protokoll: $LogFile" -ForegroundColor Yellow
    try { Stop-Transcript | Out-Null } catch {}
    exit 1
}

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Get-Package([string]$Name, [string]$Url) {
    New-Item -ItemType Directory -Force -Path $PackagesDir | Out-Null
    $destination = Join-Path $PackagesDir $Name
    if (Test-Path -LiteralPath $destination) {
        Write-Host "Verwende mitgelieferte Datei: $Name"
        return $destination
    }
    if ($Offline) {
        throw "Offline-Paket unvollständig: '$Name' fehlt in '$PackagesDir'."
    }
    Write-Host "Lade $Name ..."
    Invoke-WebRequest -Uri $Url -OutFile $destination -UseBasicParsing
    return $destination
}

function Invoke-Installer([string]$File, [string[]]$Arguments, [string]$Description) {
    Write-Host "$Description ..."
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -Wait -PassThru
    # 1638 bedeutet bei MSI-basierten Paketen: gleiche oder neuere Version
    # ist bereits installiert. Das ist für den Kiosk ein erfolgreicher Zustand.
    if ($process.ExitCode -notin @(0, 3010, 1641, 1638)) {
        throw "$Description fehlgeschlagen (Exitcode $($process.ExitCode))."
    }
    if ($process.ExitCode -eq 1638) {
        Write-Host "${Description}: gleiche oder neuere Version ist bereits vorhanden."
    }
}

function Find-FirefoxDirectory {
    $candidates = @(
        (Join-Path $env:ProgramFiles "Mozilla Firefox"),
        (Join-Path ${env:ProgramFiles(x86)} "Mozilla Firefox")
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate "firefox.exe"))) {
            return $candidate
        }
    }
    throw "Firefox wurde nach der Installation nicht gefunden."
}

if (-not (Test-Path -LiteralPath (Join-Path $KioskDir "app.py"))) {
    throw "app.py wurde in '$KioskDir' nicht gefunden."
}
if (-not (Test-Path -LiteralPath $RequirementsFile)) {
    throw "requirements.txt wurde in '$InstallerDir' nicht gefunden."
}

Write-Step "Python $PythonVersion"
$systemPython = Join-Path $env:ProgramFiles "Python312\python.exe"
if (-not (Test-Path -LiteralPath $systemPython)) {
    $pythonInstaller = Get-Package $PythonInstallerName $PythonUrl
    Invoke-Installer $pythonInstaller @(
        "/quiet", "InstallAllUsers=1", "TargetDir=$env:ProgramFiles\Python312",
        "PrependPath=1", "Include_launcher=1", "Include_pip=1", "Shortcuts=0"
    ) "Installiere Python"
}
if (-not (Test-Path -LiteralPath $systemPython)) {
    throw "Python 3.12 wurde nach der Installation nicht unter '$systemPython' gefunden."
}
$installedPythonVersion = & $systemPython -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
if ($LASTEXITCODE -ne 0 -or $installedPythonVersion.Trim() -ne "3.12") {
    throw "Nicht unterstützte Python-Version: '$installedPythonVersion'. Erforderlich ist Python 3.12."
}

Write-Step "Visual C++ Laufzeit"
$vcInstaller = Get-Package $VCRedistName $VCRedistUrl
Invoke-Installer $vcInstaller @("/install", "/quiet", "/norestart") "Installiere Visual C++ Redistributable"

Write-Step "Eigene Python-Umgebung und App-Pakete"
$recreateVenv = $false
if (Test-Path -LiteralPath $VenvPython) {
    $venvVersion = & $VenvPython -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" 2>$null
    if ($LASTEXITCODE -ne 0 -or $venvVersion.Trim() -ne "3.12") {
        Write-Host "Vorhandene Python-Umgebung verwendet nicht Python 3.12 und wird neu erstellt."
        $recreateVenv = $true
    }
}
if ($recreateVenv) {
    Remove-Item -LiteralPath $VenvDir -Recurse -Force
}
if (-not (Test-Path -LiteralPath $VenvPython)) {
    & $systemPython -m venv $VenvDir
    if ($LASTEXITCODE -ne 0) { throw "Python-Umgebung konnte nicht erstellt werden." }
}
$wheelDir = Join-Path $PackagesDir "wheels"
if ($Offline) {
    if (-not (Test-Path -LiteralPath $wheelDir)) {
        throw "Offline-Paket unvollständig: '$wheelDir' fehlt."
    }
    & $VenvPython -m pip install --no-index --find-links $wheelDir -r $RequirementsFile
} else {
    & $VenvPython -m pip install --upgrade pip
    & $VenvPython -m pip install -r $RequirementsFile
}
if ($LASTEXITCODE -ne 0) { throw "Installation der Python-Pakete ist fehlgeschlagen." }

Write-Step "HIDAPI"
$hidapiZip = Get-Package $HidapiZipName $HidapiUrl
$hidExtract = Join-Path $env:TEMP "kiosk-hidapi"
if (Test-Path -LiteralPath $hidExtract) {
    Remove-Item -LiteralPath $hidExtract -Recurse -Force
}
Expand-Archive -LiteralPath $hidapiZip -DestinationPath $hidExtract -Force
$hidDll = Get-ChildItem -LiteralPath $hidExtract -Recurse -Filter "hidapi.dll" |
    Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
    Select-Object -First 1
if (-not $hidDll) {
    $hidDll = Get-ChildItem -LiteralPath $hidExtract -Recurse -Filter "hidapi.dll" |
        Select-Object -First 1
}
if (-not $hidDll) { throw "hidapi.dll wurde im Archiv nicht gefunden." }
Copy-Item -LiteralPath $hidDll.FullName -Destination (Join-Path $VenvDir "Scripts\hidapi.dll") -Force

Write-Step "Firefox und Kiosk-Richtlinie"
$firefoxInstaller = Get-Package $FirefoxInstallerName $FirefoxUrl
Invoke-Installer $firefoxInstaller @("/S") "Installiere Firefox"
Get-Process firefox -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
$firefoxDir = Find-FirefoxDirectory
$distributionDir = Join-Path $firefoxDir "distribution"
New-Item -ItemType Directory -Force -Path $distributionDir | Out-Null
$policies = @{
    policies = @{
        Permissions = @{
            Autoplay = @{
                Default = "allow-audio-video"
                Allow = @("http://localhost:53100")
                Locked = $true
            }
        }
        Preferences = @{
            "media.autoplay.default" = @{ Value = 0; Status = "locked" }
            "media.autoplay.blocking_policy" = @{ Value = 0; Status = "locked" }
            "media.block-autoplay-until-in-foreground" = @{ Value = $false; Status = "locked" }
            "media.autoplay.block-webaudio" = @{ Value = $false; Status = "locked" }
        }
        DontCheckDefaultBrowser = $true
        DisableAppUpdate = $false
    }
} | ConvertTo-Json -Depth 6
Set-Content -LiteralPath (Join-Path $distributionDir "policies.json") -Value $policies -Encoding UTF8

Write-Step "Startdatei"
$launcher = @'
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
'@
Set-Content -LiteralPath (Join-Path $KioskDir "kiosk.bat") -Value $launcher -Encoding ASCII

Write-Step "Firefox-Kiosk-Verknüpfung"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Firefox Kiosk.lnk"
$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $firefoxDir "firefox.exe"
$shortcut.Arguments = '-kiosk -private-window "http://localhost:53100/"'
$shortcut.WorkingDirectory = $firefoxDir
$shortcut.IconLocation = "$(Join-Path $firefoxDir 'firefox.exe'),0"
$shortcut.Save()
Write-Host "Firefox-Kiosk-Verknüpfung erstellt: $shortcutPath"

Write-Step "Funktionstest"
& $VenvPython -c "import flask, flask_socketio, psutil, requests, socketio; from PIL import Image; from StreamDeck.DeviceManager import DeviceManager; print('Python-Pakete: OK'); print('Stream Decks gefunden:', len(DeviceManager().enumerate()))"
if ($LASTEXITCODE -ne 0) { throw "Der abschließende Python-Test ist fehlgeschlagen." }

Write-Host ""
Write-Host "Kiosk-Installation erfolgreich abgeschlossen." -ForegroundColor Green
Write-Host "Start: $KioskDir\kiosk.bat"
Write-Host "Die systemweite PowerShell-Ausführungsrichtlinie wurde nicht geändert."
Write-Host "Protokoll: $LogFile"
try { Stop-Transcript | Out-Null } catch {}
