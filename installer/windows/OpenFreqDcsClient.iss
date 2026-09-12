#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif

#define RepoOwner "EnragedStrings"
#define RepoName "openFreqDCSPort"

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
; The exe carries the DCS export scripts as embedded resources (see DcsExportInstaller.cs) and
; writes them out itself via "--dcs-export install" below -- no separate loose copy needed here.
Source: "..\..\artifacts\publish\client\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OpenFreq DCS Client"; Filename: "{app}\OpenFreq.Client.exe"
; /K (not /C) so the window stays open afterward -- this is the one place a human is actually
; watching and expects to see what happened, unlike the silent [Run]/[UninstallRun] steps below.
Name: "{group}\Repair DCS Export"; Filename: "{cmd}"; Parameters: "/K ""{app}\OpenFreq.Client.exe"" --dcs-export install"

[Run]
Filename: "{app}\OpenFreq.Client.exe"; Parameters: "--dcs-export install"; Flags: runhidden
Filename: "{app}\OpenFreq.Client.exe"; Description: "Launch OpenFreq DCS Client"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Runs while {app}\OpenFreq.Client.exe still exists, before Inno removes any files -- strips the
; OpenFreqDCS hook back out of every detected Export.lua so DCS doesn't keep trying (and failing)
; to dofile a script that no longer exists after uninstall.
Filename: "{app}\OpenFreq.Client.exe"; Parameters: "--dcs-export uninstall"; Flags: runhidden

[Code]
const
  FolderIdSavedGamesString = '{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}';

var
  DetectPage: TWizardPage;
  DetectMemo: TNewMemo;
  ReleaseNotesLink: TNewStaticText;

function CLSIDFromString(lpsz: WideString; var pclsid: TGUID): LongInt;
  external 'CLSIDFromString@ole32.dll stdcall';

function SHGetKnownFolderPath(const rfid: TGUID; dwFlags: DWORD; hToken: THandle;
  var ppszPath: PWideChar): LongInt;
  external 'SHGetKnownFolderPath@shell32.dll stdcall';

procedure CoTaskMemFree(pv: Pointer);
  external 'CoTaskMemFree@ole32.dll stdcall';

// Mirrors DcsExportInstaller.TryGetRealSavedGamesPath (OpenFreq.Client/Services/DcsExportInstaller.cs):
// "Saved Games" is its own relocatable Windows known folder, not derivable from the profile path by
// string concatenation alone. This copy is read-only/best-effort -- it only drives what the wizard
// *shows* the user; the actual install/uninstall always goes through the exe itself (see [Run] /
// [UninstallRun] above), so a wrong guess here can't corrupt anything, only misinform the preview.
function GetSavedGamesPath(): String;
var
  Guid: TGUID;
  PathPtr: PWideChar;
begin
  Result := '';
  if CLSIDFromString(FolderIdSavedGamesString, Guid) = 0 then
  begin
    if SHGetKnownFolderPath(Guid, 0, 0, PathPtr) = 0 then
    begin
      Result := PathPtr;
      CoTaskMemFree(PathPtr);
    end;
  end;
  if Result = '' then
    Result := ExpandConstant('{%USERPROFILE}\Saved Games');
end;

procedure CollectDcsDirectories(const Root: String; List: TStrings);
var
  FindRec: TFindRec;
begin
  if FindFirst(Root + '\DCS*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          List.Add(Root + '\' + FindRec.Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure ReleaseNotesLinkClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open',
    'https://github.com/{#RepoOwner}/{#RepoName}/releases/tag/v{#AppVersion}',
    '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure InitializeWizard();
var
  SavedGamesRoot: String;
  Detected: TStringList;
  I: Integer;
begin
  DetectPage := CreateCustomPage(wpSelectDir, 'DCS Export',
    'These locations are detected automatically -- nothing to configure.');

  DetectMemo := TNewMemo.Create(DetectPage);
  DetectMemo.Parent := DetectPage.Surface;
  DetectMemo.Left := 0;
  DetectMemo.Top := 0;
  DetectMemo.Width := DetectPage.SurfaceWidth;
  DetectMemo.Height := ScaleY(140);
  DetectMemo.ScrollBars := ssVertical;
  DetectMemo.ReadOnly := True;

  SavedGamesRoot := GetSavedGamesPath();
  Detected := TStringList.Create;
  try
    CollectDcsDirectories(SavedGamesRoot, Detected);

    if Detected.Count = 0 then
    begin
      DetectMemo.Lines.Add('No existing DCS installation was detected under:');
      DetectMemo.Lines.Add(SavedGamesRoot);
      DetectMemo.Lines.Add('');
      DetectMemo.Lines.Add('That''s fine -- the export installs automatically the first time you ' +
        'run OpenFreq DCS Client after DCS itself has created its Saved Games folder.');
    end
    else
    begin
      DetectMemo.Lines.Add('The OpenFreq radio export will be installed into:');
      DetectMemo.Lines.Add('');
      for I := 0 to Detected.Count - 1 do
        DetectMemo.Lines.Add('  ' + Detected[I]);
    end;
  finally
    Detected.Free;
  end;

  ReleaseNotesLink := TNewStaticText.Create(DetectPage);
  ReleaseNotesLink.Parent := DetectPage.Surface;
  ReleaseNotesLink.Caption := 'View full release notes for v{#AppVersion} online';
  ReleaseNotesLink.Cursor := crHand;
  ReleaseNotesLink.Font.Style := [fsUnderline];
  ReleaseNotesLink.Font.Color := clHighlight;
  ReleaseNotesLink.Left := 0;
  ReleaseNotesLink.Top := DetectMemo.Top + DetectMemo.Height + ScaleY(12);
  ReleaseNotesLink.OnClick := @ReleaseNotesLinkClick;
end;

function IsClientRunning(): Boolean;
var
  ResultCode: Integer;
  TempFile: String;
  Output: TArrayOfString;
  I: Integer;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\openfreq-tasklist.txt');
  if Exec(ExpandConstant('{cmd}'),
    '/C tasklist /FI "IMAGENAME eq OpenFreq.Client.exe" /NH > "' + TempFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TempFile, Output) then
      for I := 0 to GetArrayLength(Output) - 1 do
        if Pos('OpenFreq.Client.exe', Output[I]) > 0 then
        begin
          Result := True;
          Break;
        end;
  end;
  DeleteFile(TempFile);
end;

function EnsureClientNotRunning(): Boolean;
begin
  Result := True;
  while IsClientRunning() do
  begin
    if MsgBox('OpenFreq DCS Client is currently running.' + #13#10 +
      'Please close it, then click Retry to continue.', mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := EnsureClientNotRunning();
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureClientNotRunning();
end;
