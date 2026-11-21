# Kiosk 3 - installs everything the kiosk needs (called by the setup, can be run again by hand as administrator)
# - Python 3.12 (needed for the StreamDeck driver), all users, added to PATH
# - Visual C++ runtime, hidapi.dll (StreamDeck driver), Python packages
# - optional: Firefox (autoplay allowed)
param(
  [string]$InstallDir = "C:\kiosk3",
  [int]$Deps = 1,
  [int]$Firefox = 0
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$PythonVersion = "3.12.6"
$PythonUrl     = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-amd64.exe"
$VcRedistUrl   = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
$HidapiUrl     = "https://github.com/libusb/hidapi/releases/download/hidapi-0.15.0/hidapi-win.zip"
$FirefoxUrl    = "https://download.mozilla.org/?product=firefox-latest&os=win64&lang=de"
$FirefoxDir    = "C:\Program Files\Mozilla Firefox"

$Tmp = Join-Path $env:TEMP "kiosk3-setup"
New-Item -ItemType Directory -Force -Path $Tmp | Out-Null
$LogDir = Join-Path $InstallDir "Setup"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
Start-Transcript -Path (Join-Path $LogDir "install.log") -Force | Out-Null

$failures = New-Object System.Collections.ArrayList

function Step($text) { Write-Host ""; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Fail($text) { Write-Host "FAILED: $text" -ForegroundColor Red; [void]$failures.Add($text) }

function Download($url, $dest) {
  if (Test-Path $dest) { Remove-Item $dest -Force }
  Write-Host "Downloading $url"
  Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
  if (-not (Test-Path $dest) -or (Get-Item $dest).Length -lt 100000) { throw "Download incomplete: $url" }
}

function Find-Python312 {
  foreach ($key in "HKLM:\SOFTWARE\Python\PythonCore\3.12\InstallPath", "HKCU:\SOFTWARE\Python\PythonCore\3.12\InstallPath") {
    try {
      $dir = (Get-ItemProperty -Path $key -ErrorAction Stop).'(default)'
      if ($dir -and (Test-Path (Join-Path $dir "python.exe"))) { return (Join-Path $dir "python.exe") }
    } catch { }
  }
  foreach ($p in "C:\Program Files\Python312\python.exe", "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe") {
    if (Test-Path $p) { return $p }
  }
  return $null
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
  Write-Host "Please run this script as administrator." -ForegroundColor Red
  Stop-Transcript | Out-Null
  exit 1
}

if ($Deps -eq 1) {

  # --- Python 3.12 ---
  Step "Python $PythonVersion"
  $py = Find-Python312
  if ($py) {
    Write-Host "Python 3.12 already installed: $py"
  } else {
    try {
      $setup = Join-Path $Tmp "python-$PythonVersion-amd64.exe"
      Download $PythonUrl $setup
      Write-Host "Installing Python $PythonVersion (all users, PATH) ..."
      $p = Start-Process -FilePath $setup -ArgumentList "/quiet","InstallAllUsers=1","PrependPath=1","Include_launcher=1","Include_pip=1","Shortcuts=0" -Wait -PassThru
      if ($p.ExitCode -ne 0) { throw "Python installer exit code $($p.ExitCode)" }
      $py = Find-Python312
      if (-not $py) { throw "python.exe not found after installation" }
    } catch { Fail "Python: $_" }
  }

  # --- Visual C++ runtime ---
  Step "Visual C++ runtime"
  try {
    $vc = Join-Path $Tmp "vc_redist.x64.exe"
    Download $VcRedistUrl $vc
    $p = Start-Process -FilePath $vc -ArgumentList "/install","/quiet","/norestart" -Wait -PassThru
    # 0 = ok, 1638 = newer version present, 3010 = ok but restart recommended
    if (@(0, 1638, 3010) -notcontains $p.ExitCode) { throw "exit code $($p.ExitCode)" }
  } catch { Fail "VC++ runtime: $_" }

  # --- .NET 10 Desktop Runtime (needed by the OMT output, omt\PresenterOmt.exe) ---
  Step ".NET 10 Desktop Runtime"
  try {
    $haveDotnet = $false
    try { $haveDotnet = [bool]((& dotnet --list-runtimes 2>$null) -match 'Microsoft\.WindowsDesktop\.App 10\.') } catch { }
    if ($haveDotnet) { Write-Host ".NET 10 Desktop Runtime already installed" }
    else {
      $dn = Join-Path $Tmp "windowsdesktop-runtime-10-win-x64.exe"
      Download "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe" $dn
      $p = Start-Process -FilePath $dn -ArgumentList "/install","/quiet","/norestart" -Wait -PassThru
      if (@(0, 1638, 3010) -notcontains $p.ExitCode) { throw "exit code $($p.ExitCode)" }
    }
  } catch { Fail ".NET 10 Desktop Runtime: $_" }

  # --- Python packages ---
  Step "Python packages"
  if ($py) {
    try {
      & $py -m pip install --upgrade pip
      & $py -m pip install flask flask-socketio psutil requests "python-socketio" websocket-client pillow streamdeck
      if ($LASTEXITCODE -ne 0) { throw "pip exit code $LASTEXITCODE" }
    } catch { Fail "pip packages: $_" }
    # optional extra, not needed if hidapi.dll is present
    & $py -m pip install hidapi 2>&1 | Out-Null
  } else { Fail "Python packages skipped (no Python)" }

  # --- hidapi.dll for the StreamDeck ---
  Step "StreamDeck driver (hidapi.dll)"
  try {
    $zip = Join-Path $Tmp "hidapi-win.zip"
    Download $HidapiUrl $zip
    $ex = Join-Path $Tmp "hidapi"
    if (Test-Path $ex) { Remove-Item $ex -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $ex -Force
    $dll = Get-ChildItem -Path $ex -Recurse -Filter "hidapi.dll" | Where-Object { $_.FullName -match "(\\|/)x64(\\|/)" } | Select-Object -First 1
    if (-not $dll) { $dll = Get-ChildItem -Path $ex -Recurse -Filter "hidapi.dll" | Select-Object -First 1 }
    if (-not $dll) { throw "hidapi.dll not found in the download" }
    try {
      Copy-Item $dll.FullName "C:\Windows\System32\hidapi.dll" -Force -ErrorAction Stop
      Write-Host "hidapi.dll -> C:\Windows\System32"
    } catch {
      if (-not $py) { throw "System32 not writable and no Python folder known" }
      Copy-Item $dll.FullName (Join-Path (Split-Path $py) "hidapi.dll") -Force
      Write-Host "hidapi.dll -> Python folder"
    }
  } catch { Fail "hidapi.dll: $_" }

  # --- quick test ---
  if ($py) {
    Step "Test"
    $test = Join-Path $Tmp "test_deck.py"
    Set-Content -Path $test -Encoding ASCII -Value "from StreamDeck.DeviceManager import DeviceManager`nprint('StreamDeck devices found: %d' % len(DeviceManager().enumerate()))`nimport flask, flask_socketio, psutil`nprint('Flask packages OK')"
    & $py $test
    if ($LASTEXITCODE -ne 0) { Fail "Test script failed (StreamDeck driver or packages)" }
  }
}

# --- Firefox ---
if ($Firefox -eq 1) {
  Step "Firefox"
  try {
    if (-not (Test-Path (Join-Path $FirefoxDir "firefox.exe"))) {
      $ff = Join-Path $Tmp "FirefoxSetup.exe"
      Download $FirefoxUrl $ff
      $p = Start-Process -FilePath $ff -ArgumentList "/S" -Wait -PassThru
      if ($p.ExitCode -ne 0) { throw "Firefox installer exit code $($p.ExitCode)" }
    } else { Write-Host "Firefox already installed" }
    Write-Host "Firefox ready. Kiosk3 applies its bundled audio profile on first start."
  } catch { Fail "Firefox: $_" }
}

Write-Host ""
if ($failures.Count -eq 0) {
  Write-Host "All done." -ForegroundColor Green
  Stop-Transcript | Out-Null
  Start-Sleep -Seconds 3
  exit 0
} else {
  Write-Host "Finished with problems:" -ForegroundColor Yellow
  $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Yellow }
  Write-Host "Log file: $(Join-Path $LogDir 'install.log')"
  Write-Host "You can run this script again later (as administrator): $PSCommandPath"
  Stop-Transcript | Out-Null
  Read-Host "Press Enter to continue"
  exit 0
}
