#ifndef AppVersion
  #error AppVersion is required
#endif
[Setup]
AppId={{D1B3E1CF-71E7-4B12-94A9-C5EB268CD775}
AppName=AICountsManager
AppVersion={#AppVersion}
AppPublisher=Jorge Martin
DefaultDirName={localappdata}\Programs\AICountsManager
DefaultGroupName=AICountsManager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.19041
ArchitecturesAllowed={#TargetArchitecture}
ArchitecturesInstallIn64BitMode={#TargetArchitecture}
OutputDir={#OutputDir}
OutputBaseFilename={#PackageName}-Setup
SetupIconFile={#PackageDir}\assets\icon.ico
UninstallDisplayIcon={app}\AICountsManager.exe
Compression=lzma2/fast
SolidCompression=yes
LZMANumBlockThreads=2
WizardStyle=modern
AppMutex=Local\AICountsManager.SingleInstance
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName=AICountsManager
VersionInfoDescription=AICountsManager Setup

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; Flags: unchecked
Name: "startup"; Description: "Iniciar AICountsManager con Windows"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AICountsManager"; Filename: "{app}\AICountsManager.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\AICountsManager"; Filename: "{app}\AICountsManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\AICountsManager"; Filename: "{app}\AICountsManager.exe"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\AICountsManager.exe"; Description: "Abrir AICountsManager"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userstartup}\AICountsManager.lnk"
; User profiles, settings and history in AppData are deliberately preserved on uninstall.
