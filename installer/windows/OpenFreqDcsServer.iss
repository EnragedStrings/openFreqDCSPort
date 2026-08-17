#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{A7F00A02-9181-4B38-9E8B-6CF0B618DCC1}
AppName=OpenFreq DCS Server
AppVersion={#AppVersion}
AppPublisher=OpenFreq
DefaultDirName={autopf}\OpenFreq DCS Server
DefaultGroupName=OpenFreq DCS Server
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\installers
OutputBaseFilename=OpenFreq-DCS-Server-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\OpenFreq.Server.exe

[Files]
Source: "..\..\artifacts\publish\server\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OpenFreq DCS Server"; Filename: "{app}\OpenFreq.Server.exe"

[Run]
Filename: "{app}\OpenFreq.Server.exe"; Description: "Launch OpenFreq DCS Server"; Flags: nowait postinstall skipifsilent
