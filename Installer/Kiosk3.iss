; Kiosk 3 setup - compile with Inno Setup (ISCC.exe kiosk3.iss)
; The application files are taken from SrcDir (the finished kiosk3 folder).

#define AppName "Kiosk 3"
#define AppVersion "3.15"
#define SrcDir ".."

[Setup]
AppId={{7E1DAB40-4148-4C15-88A9-172A7E2C91D7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Peter Aellig
DefaultDirName=C:\kiosk3
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=Kiosk3_Setup_v{#AppVersion}
Compression=zip
SolidCompression=no
SetupIconFile={#SrcDir}\Images\kiosk.ico
UninstallDisplayIcon={app}\Images\kiosk.ico
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "chromeaudio"; Description: "Allow automatic sound in Chrome for Kiosk 3 addresses (TCP 53101)"
Name: "deps"; Description: "Install Python 3.12, Python packages, StreamDeck driver and VC++ runtime (internet needed)"
Name: "firefox"; Description: "Install Firefox as kiosk browser (autoplay with sound allowed, internet needed)"
Name: "firewall"; Description: "Allow network access to the kiosk (Windows Firewall, TCP port 53101) so remote devices can connect"
Name: "desktopicon"; Description: "Create a desktop icon to start the kiosk"
Name: "autostart"; Description: "Start the kiosk automatically when Windows starts"; Flags: unchecked

[Files]
Source: "{#SrcDir}\logo.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\kiosk.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\kiosk_chrome.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\kiosk_start.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\kiosk_runtime.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\kiosk_preferences.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\admin_access.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\app.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\app_streamdeck.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\firefox-profile\user.js"; DestDir: "{app}\firefox-profile"; Flags: ignoreversion
Source: "{#SrcDir}\Images\kiosk.ico"; DestDir: "{app}\Images"; Flags: ignoreversion
Source: "{#SrcDir}\static\presentation.html"; DestDir: "{app}\static"; Flags: ignoreversion
Source: "{#SrcDir}\static\api_help.html"; DestDir: "{app}\static"; Flags: ignoreversion
Source: "{#SrcDir}\static\player.js"; DestDir: "{app}\static"; Flags: ignoreversion
Source: "{#SrcDir}\static\socket.io.min.js"; DestDir: "{app}\static"; Flags: ignoreversion
Source: "{#SrcDir}\static\style.css"; DestDir: "{app}\static"; Flags: ignoreversion
Source: "{#SrcDir}\views\remote.html"; DestDir: "{app}\views"; Flags: ignoreversion
Source: "{#SrcDir}\views\admin.html"; DestDir: "{app}\views"; Flags: ignoreversion
Source: "{#SrcDir}\licence\OMT-LICENSE.txt"; DestDir: "{app}\licence"; Flags: ignoreversion
Source: "{#SrcDir}\licence\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}\licence"; Flags: ignoreversion
Source: "{#SrcDir}\static\videos\*.mp4"; DestDir: "{app}\static\videos"; Flags: ignoreversion
Source: "{#SrcDir}\static\videos_playlist\*.mp4"; DestDir: "{app}\static\videos_playlist"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild1\Evakuierung_D.png"; DestDir: "{app}\static\bild1"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild1\Evakuierung_E.png"; DestDir: "{app}\static\bild1"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild1\Evakuierung_F.png"; DestDir: "{app}\static\bild1"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild2\Broadcast_unterbrochen-E_D_F.png"; DestDir: "{app}\static\bild2"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild3\Artist_kann_nicht_auftreten_E-D_F.png"; DestDir: "{app}\static\bild3"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild4\lockdown_E_D_F.png"; DestDir: "{app}\static\bild4"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild5\Message5.jpg"; DestDir: "{app}\static\bild5"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild6\colorBars75.jpg"; DestDir: "{app}\static\bild6"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild6\message6.png"; DestDir: "{app}\static\bild6"; Flags: ignoreversion
Source: "{#SrcDir}\static\bild7\message7.png"; DestDir: "{app}\static\bild7"; Flags: ignoreversion
Source: "{#SrcDir}\omt\*.dll"; DestDir: "{app}\omt"; Flags: recursesubdirs ignoreversion
Source: "{#SrcDir}\omt\*.exe"; DestDir: "{app}\omt"; Flags: recursesubdirs ignoreversion
Source: "{#SrcDir}\omt\*.deps.json"; DestDir: "{app}\omt"; Flags: recursesubdirs ignoreversion
Source: "{#SrcDir}\omt\*.runtimeconfig.json"; DestDir: "{app}\omt"; Flags: recursesubdirs ignoreversion
Source: "{#SrcDir}\button_config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
; Setup helpers
Source: "install_deps.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion

[Icons]
Name: "{commondesktop}\Kiosk 3 Start"; Filename: "{app}\kiosk.bat"; WorkingDir: "{app}"; IconFilename: "{app}\Images\kiosk.ico"; Tasks: desktopicon
Name: "{commonstartup}\Kiosk 3"; Filename: "{app}\kiosk.bat"; WorkingDir: "{app}"; IconFilename: "{app}\Images\kiosk.ico"; Tasks: autostart
Name: "{group}\Kiosk 3 Start"; Filename: "{app}\kiosk.bat"; WorkingDir: "{app}"; IconFilename: "{app}\Images\kiosk.ico"
Name: "{group}\Kiosk 3 Start (Chrome)"; Filename: "{app}\kiosk_chrome.bat"; WorkingDir: "{app}"; IconFilename: "{app}\Images\kiosk.ico"
Name: "{group}\Kiosk 3 Admin (this computer)"; Filename: "{cmd}"; Parameters: "/c start """" http://localhost:53101/admin"; Flags: runminimized; IconFilename: "{app}\Images\kiosk.ico"
Name: "{group}\Kiosk 3 Folder"; Filename: "{app}"
Name: "{group}\Uninstall Kiosk 3"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "{code:DepsParams}"; StatusMsg: "Installing Python, drivers and programs (this can take a few minutes) ..."; Flags: waituntilterminated; Check: AnyDepsSelected
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Kiosk 3 (TCP 53101)"""; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Kiosk 3 (TCP 53101)"" dir=in action=allow protocol=TCP localport=53101 profile=any"; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{app}\kiosk.bat"; Description: "Start the kiosk now"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Kiosk 3 (TCP 53101)"""; Flags: runhidden; RunOnceId: "DelFirewall"

[Code]
function B2I(B: Boolean): String;
begin
  if B then Result := '1' else Result := '0';
end;

function AnyDepsSelected: Boolean;
begin
  Result := WizardIsTaskSelected('deps') or WizardIsTaskSelected('firefox');
end;

function DepsParams(Param: String): String;
begin
  Result := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Setup\install_deps.ps1') + '"'
          + ' -InstallDir "' + ExpandConstant('{app}') + '"'
          + ' -Deps ' + B2I(WizardIsTaskSelected('deps'))
          + ' -Firefox ' + B2I(WizardIsTaskSelected('firefox'));
end;


const
  ChromeAudioKey = 'SOFTWARE\Policies\Google\Chrome\AutoplayAllowlist';
  OwnedAudioKey = 'SOFTWARE\Kiosk3\Setup\ChromeAudio';

procedure AddKioskOrigin(const Origin: String);
var Names: TArrayOfString; I: Integer; Slot, Existing: String;
begin
  if RegGetValueNames(HKLM64, ChromeAudioKey, Names) then
    for I := 0 to GetArrayLength(Names)-1 do
      if RegQueryStringValue(HKLM64, ChromeAudioKey, Names[I], Existing) and
         (CompareText(Existing, Origin) = 0) then Exit;
  I := 1;
  while RegValueExists(HKLM64, ChromeAudioKey, IntToStr(I)) do I := I + 1;
  Slot := IntToStr(I);
  if not RegWriteStringValue(HKLM64, ChromeAudioKey, Slot, Origin) then
    RaiseException('Could not configure Chrome autoplay for ' + Origin);
  if not RegWriteStringValue(HKLM64, OwnedAudioKey, Slot, Origin) then begin
    RegDeleteValue(HKLM64, ChromeAudioKey, Slot);
    RaiseException('Could not record the Chrome autoplay entry.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Output: TExecOutput; Code, I: Integer; Address: String;
begin
  if (CurStep <> ssPostInstall) or not WizardIsTaskSelected('chromeaudio') then Exit;
  AddKioskOrigin('http://192.168.1.113:53101');
  AddKioskOrigin('http://localhost:53101');
  AddKioskOrigin('http://127.0.0.1:53101');
  AddKioskOrigin('http://' + GetComputerNameString + ':53101');
  if ExecAndCaptureOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "[Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()) | Where-Object { $_.AddressFamily -eq ''InterNetwork'' } | ForEach-Object { $_.IPAddressToString }"',
      '', SW_HIDE, ewWaitUntilTerminated, Code, Output) and (Code = 0) then
    for I := 0 to GetArrayLength(Output.StdOut)-1 do begin
      Address := Trim(Output.StdOut[I]);
      if (Address <> '') and (Pos('169.254.', Address) <> 1) then
        AddKioskOrigin('http://' + Address + ':53101');
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Names: TArrayOfString; I: Integer; Owned, Existing: String;
begin
  if (CurUninstallStep <> usUninstall) or not RegGetValueNames(HKLM64, OwnedAudioKey, Names) then Exit;
  for I := 0 to GetArrayLength(Names)-1 do begin
    if RegQueryStringValue(HKLM64, OwnedAudioKey, Names[I], Owned) and
       RegQueryStringValue(HKLM64, ChromeAudioKey, Names[I], Existing) and (Owned = Existing) then
      RegDeleteValue(HKLM64, ChromeAudioKey, Names[I]);
    RegDeleteValue(HKLM64, OwnedAudioKey, Names[I]);
  end;
  RegDeleteKeyIfEmpty(HKLM64, OwnedAudioKey);
  RegDeleteKeyIfEmpty(HKLM64, ChromeAudioKey);
end;
