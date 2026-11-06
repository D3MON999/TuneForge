; Installs to Program Files, so only admins can change the app and its elevated engine.
#ifndef AppVersion
  #error Build this with release.ps1
#endif

[Setup]
AppId={{7E0C2B6A-3D51-4F7C-9B1E-5A2D8C4F1E77}
AppName=TuneForge
AppVersion={#AppVersion}
AppPublisher=TuneForge
DefaultDirName={autopf}\TuneForge
; The engine only runs from Program Files, so the install folder can't be changed.
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputFolder}
OutputBaseFilename=TuneForge-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SourcePath}..\assets\TuneForge.ico
UninstallDisplayIcon={app}\TuneForge.exe
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#AppFolder}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "update-task.xml"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TuneForge"; Filename: "{app}\TuneForge.exe"
Name: "{autodesktop}\TuneForge"; Filename: "{app}\TuneForge.exe"; Tasks: desktopicon

[Registry]
; Removes Start with Windows on uninstall, without ever turning it on here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "TuneForge"; Flags: dontcreatekey uninsdeletevalue

[Run]
; Checks for updates at startup and every 6 hours, then exits. Nothing stays running.
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""TuneForge Updater"" /XML ""{app}\update-task.xml"" /F"; Flags: runhidden
Filename: "{app}\TuneForge.exe"; Description: "Open TuneForge"; Flags: nowait postinstall skipifsilent runasoriginaluser
; An update from the app runs silently, so this reopens the app afterwards.
Filename: "{app}\TuneForge.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent and not IsAutoUpdate
; An automatic update runs as SYSTEM, which can't open the app. Removing this tells the app it's safe to reopen.
Filename: "{cmd}"; Parameters: "/c del /q ""{commonappdata}\TuneForge\update-starting"""; Flags: runhidden; Check: IsAutoUpdate

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""TuneForge Updater"" /F"; Flags: runhidden; RunOnceId: "RemoveUpdater"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\TuneForge\Updates"

[Code]
const
  Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function IsAutoUpdate: Boolean;
begin
  Result := ExpandConstant('{param:autoupdate|0}') = '1';
end;

function HasWebView2: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not HasWebView2 and not WizardSilent then
    MsgBox('TuneForge needs the Microsoft Edge WebView2 Runtime, which is missing on this PC.' + #13#10 +
      'Install it from Microsoft: https://developer.microsoft.com/microsoft-edge/webview2/', mbInformation, MB_OK);
end;
