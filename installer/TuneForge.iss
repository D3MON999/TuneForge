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

[InstallDelete]
; Old page files have new names each build, so they'd otherwise pile up with every update.
Type: filesandordirs; Name: "{app}\wwwroot"

[Files]
Source: "{#AppFolder}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TuneForge"; Filename: "{app}\TuneForge.exe"
Name: "{autodesktop}\TuneForge"; Filename: "{app}\TuneForge.exe"; Tasks: desktopicon

[Registry]
; Removes Start with Windows on uninstall, without ever turning it on here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "TuneForge"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\TuneForge.exe"; Description: "Open TuneForge"; Flags: nowait postinstall skipifsilent runasoriginaluser
; An update runs silently, so this reopens the app afterwards.
Filename: "{app}\TuneForge.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]
const
  Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

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
