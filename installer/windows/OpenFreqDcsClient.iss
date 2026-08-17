#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{A7F00A01-9181-4B38-9E8B-6CF0B618DCC1}
AppName=OpenFreq DCS Client
AppVersion={#AppVersion}
AppPublisher=OpenFreq
DefaultDirName={localappdata}\OpenFreq DCS Client
DefaultGroupName=OpenFreq DCS Client
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\installers
OutputBaseFilename=OpenFreq-DCS-Client-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\OpenFreq.Client.exe

[Files]
Source: "..\..\artifacts\publish\client\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\DCS\OpenFreqDCS\*"; DestDir: "{app}\DCS\OpenFreqDCS"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-DcsExport.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Icons]
Name: "{group}\OpenFreq DCS Client"; Filename: "{app}\OpenFreq.Client.exe"
Name: "{group}\Install DCS Export"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\Install-DcsExport.ps1"" -SourceRoot ""{app}\DCS\OpenFreqDCS"""

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\Install-DcsExport.ps1"" -SourceRoot ""{app}\DCS\OpenFreqDCS"""; Flags: runhidden
Filename: "{app}\OpenFreq.Client.exe"; Description: "Launch OpenFreq DCS Client"; Flags: nowait postinstall skipifsilent
