; KUDUDIFF installer (Inno Setup 6)
; Installs for the current Windows user only, so installing never asks for administrator rights.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\KUDUDIFF"
#endif

#define MyAppName "KUDUDIFF"
#define MyAppExe "KUDUDIFF.exe"
#define MyAppUrl "https://github.com/azeembukhariprivate/kududiff"

[Setup]
AppId={{F9B2A431-9DF1-458C-AE6D-2570B59A2A0B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Azeem Bukhari
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=KUDUDIFF-{#MyAppVersion}-setup
SetupIconFile=..\src\KUDUDIFF\Assets\KUDUDIFF.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Azeem Bukhari
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "contextmenu"; Description: "Add ""Compare with KUDUDIFF"" to files' right-click menu"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "self-test.log,screenshots\*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Parameters: "--register-shell"; Tasks: contextmenu; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExe}"; Description: "Open KUDUDIFF now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "KUDUDIFFUnregister"

[Registry]
; Remove saved settings when uninstalling.
Root: HKCU; Subkey: "Software\KUDUDIFF"; Flags: uninsdeletekey dontcreatekey
