; Inno Setup script for Aryan eBook Library (WinUI 3, .NET 10, Windows App SDK 2.5).
;
; Payload is an unpackaged, self-contained Release publish (app + WinAppSDK + .NET runtime),
; so the target machine needs nothing pre-installed.
;
; Build both steps at once with:  pwsh -File tools\build_installer.ps1
; Or compile alone with:          ISCC.exe installer\AryanEbookLibrary.iss   (after publishing first)

#define AppName    "Aryan eBook Library"
#define Publisher  "Aung Ko Ko"
#define ExeName    "AryanEbookLibrary.exe"
#define SrcDir     "..\publish\installer-payload"
#define AppIcon    "..\Assets\app.ico"

; Version comes off the published exe, which gets it from <Version> in AryanEbookLibrary.csproj,
; so the installer and the app's About line can never disagree (Ayaan PDF once said 1.15.0 in
; the installer and 1.0.0 in the app). Compiling before publishing fails loudly here.
#define AppVersion GetStringFileInfo(SrcDir + "\" + ExeName, "ProductVersion")

[Setup]
; Stable identity for upgrades. Never change this across versions.
AppId={{A411A052-9A90-4579-8B49-5DFFC1F4ED7B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://aungkokomm.github.io/aryan/
AppSupportURL=https://github.com/aungkokomm/AryanEbookLibrary/issues
AppUpdatesURL=https://github.com/aungkokomm/AryanEbookLibrary/releases
AppComments=Portable, offline-aware eBook catalog for Windows
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=AryanEbookLibrary-Setup-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}
; Per-user install into %LOCALAPPDATA%\Programs: no admin/UAC prompt, and the app can write its
; library (AryanLibrary-Data) next to the exe, which is what keeps it portable. Deliberately NO
; PrivilegesRequiredOverridesAllowed: a silent install has nobody to ask and would pick all-users,
; half machine-wide and needing UAC (learned on Ayaan PDF).
PrivilegesRequired=lowest
DisableDirPage=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; If the app is running during an upgrade, close it through the Restart Manager so the new exe
; really replaces the old one.
CloseApplications=yes
RestartApplications=no
; Two ways to set Aryan up, asked on the first page: installed for this user, or portable in a folder
; the user picks (a USB drive, D:\). Portable writes nothing to Windows: no uninstaller, no entry in
; Settings > Apps, no Start menu. Silent portable copy: Setup.exe /VERYSILENT /PORTABLE /DIR="E:\Aryan"
Uninstallable=not IsPortable
CreateUninstallRegKey=not IsPortable

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; The entire self-contained publish output.
;
; EXCLUDES AryanLibrary-Data, and this matters: it is the user's library (index, covers,
; settings, log) that the app creates next to the exe whenever it runs from a folder. If a
; publish folder was ever run, shipping it would push an empty library onto every install,
; and an upgrade would overwrite the user's real one.
Source: "{#SrcDir}\*"; DestDir: "{app}"; Excludes: "AryanLibrary-Data,AryanLibrary-Data\*"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; None for a portable copy: it leaves no trace in Windows.
Name: "{group}\{#AppName}";           Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Check: not IsPortable
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Check: not IsPortable
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsPortable

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

; NO [UninstallDelete], on purpose. The user's library (AryanLibrary-Data) lives inside {app}.
; Inno removes only the files it installed, and that folder is excluded from the payload, so an
; uninstall leaves the library (favorites, notes, reading progress, covers) in place for a
; reinstall. An UninstallDelete of {app} would erase it.

[Code]
var
  ModePage: TInputOptionWizardPage;
  InstalledDir: String;
  InstalledDirLabel: String;
  InstalledBrowseLabel: String;
  DirOnCommandLine: Boolean;
  // A portable copy of Aryan already on this PC, which an update goes back to.
  FoundDir: String;

function GetDriveType(Root: String): Cardinal; external 'GetDriveTypeW@kernel32.dll stdcall';

function PortableOnCommandLine: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/PORTABLE') = 0 then
      Result := True;
end;

function IsPortable: Boolean;
begin
  Result := (ModePage <> nil) and ModePage.Values[1];
end;

function PortableDir: String;
begin
  if FoundDir <> '' then
    Result := FoundDir
  else
    // Not Documents: that is often a OneDrive folder, and syncing the library's database and covers would fight the app.
    Result := ExpandConstant('{%USERPROFILE}\{#AppName}');
end;

// ---- Finding the copy an update belongs to ----
//
// A portable copy writes nothing to Windows, so Windows cannot say where it is, and every update offered
// "Install for me" and a new folder: Next, Next put a second Aryan, with an empty library, beside the reader's
// own. Setup looks for it instead: the copy that is running, or else the copy whose library was used last,
// in the places people keep one.

// The folder Aryan runs from, if it is running now.
function RunningCopy: String;
var
  Locator, Service, Found: Variant;
  I: Integer;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''{#ExeName}''');
    for I := 0 to Found.Count - 1 do
      try
        Result := ExtractFileDir(Found.ItemIndex(I).ExecutablePath);
      except
      end;
  except
    Result := '';
  end;
end;

// Keeps Dir when it holds Aryan and a library used more recently than the best so far.
procedure Consider(Dir: String; var Best: String; var BestTime: Int64);
var
  R: TFindRec;
  T: Int64;
begin
  Dir := RemoveBackslashUnlessRoot(Dir);
  if not FileExists(AddBackslash(Dir) + '{#ExeName}') then
    exit;
  if not FindFirst(AddBackslash(Dir) + 'AryanLibrary-Data\library.db', R) then
    exit;
  T := (Int64(R.LastWriteTime.dwHighDateTime) shl 32) or R.LastWriteTime.dwLowDateTime;
  FindClose(R);
  if (Best = '') or (T > BestTime) then
  begin
    Best := Dir;
    BestTime := T;
  end;
end;

// Parent itself and every folder directly inside it.
procedure ConsiderInside(Parent: String; var Best: String; var BestTime: Int64);
var
  R: TFindRec;
begin
  Consider(Parent, Best, BestTime);
  if FindFirst(AddBackslash(Parent) + '*', R) then
  try
    repeat
      if ((R.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (R.Name <> '.') and (R.Name <> '..') then
        Consider(AddBackslash(Parent) + R.Name, Best, BestTime);
    until not FindNext(R);
  finally
    FindClose(R);
  end;
end;

function ExistingCopy: String;
var
  BestTime: Int64;
  I: Integer;
  D: String;
begin
  Result := RunningCopy;
  if Result <> '' then
    exit;
  BestTime := 0;
  Consider(InstalledDir, Result, BestTime);
  ConsiderInside(ExpandConstant('{%USERPROFILE}'), Result, BestTime);
  ConsiderInside(ExpandConstant('{userdesktop}'), Result, BestTime);
  ConsiderInside(ExpandConstant('{userdocs}'), Result, BestTime);
  // Every fixed or removable drive (2 and 3), never a network or CD drive.
  for I := Ord('C') to Ord('Z') do
  begin
    D := Chr(I) + ':\';
    if (GetDriveType(D) = 2) or (GetDriveType(D) = 3) then
      ConsiderInside(D, Result, BestTime);
  end;
end;

procedure InitializeWizard;
var
  Choice: String;
begin
  InstalledDir := WizardForm.DirEdit.Text;
  InstalledDirLabel := WizardForm.SelectDirLabel.Caption;
  InstalledBrowseLabel := WizardForm.SelectDirBrowseLabel.Caption;
  // A folder given with /DIR is never swapped for a default, and a silent Setup does as it is told.
  DirOnCommandLine := ExpandConstant('{param:DIR|}') <> '';
  if not WizardSilent and not DirOnCommandLine then
  begin
    FoundDir := ExistingCopy;
    // The installed copy is the one Windows already remembers, and Inno offers it already.
    if CompareText(FoundDir, RemoveBackslashUnlessRoot(InstalledDir)) = 0 then
      FoundDir := '';
  end;

  Choice := 'Aryan is portable either way: your library (books found, covers, notes, highlights and settings) is kept in ' +
    'a folder called AryanLibrary-Data beside the app, so the whole folder can be copied to another drive or PC.';
  if FoundDir <> '' then
    Choice := 'Aryan is already in ' + FoundDir + ', so Portable is chosen: the app is updated there and its library is kept.';
  ModePage := CreateInputOptionPage(wpWelcome,
    'How do you want to use Aryan?',
    'Install it on this computer, or keep it portable.',
    Choice, True, False);
  ModePage.Add('Install for me. Aryan gets a Start menu entry and can be removed from Settings > Apps.');
  ModePage.Add('Portable. Copy Aryan into a folder you choose, such as a USB drive. Nothing is written to Windows.');
  ModePage.Values[0] := not PortableOnCommandLine and (FoundDir = '');
  ModePage.Values[1] := PortableOnCommandLine or (FoundDir <> '');
  if (PortableOnCommandLine or (FoundDir <> '')) and not DirOnCommandLine then
    WizardForm.DirEdit.Text := PortableDir;
end;

function HoldsAryan(Dir: String): Boolean;
begin
  Result := FileExists(AddBackslash(Dir) + '{#ExeName}') or DirExists(AddBackslash(Dir) + 'AryanLibrary-Data');
end;

// Browse adds "Aryan eBook Library" to the folder picked, so picking the folder that already holds Aryan and its
// library (D:\My Ebooks Data) would put a second copy, with a new empty library, inside it. Ask instead.
function CheckFolderAboveHoldsAryan: Boolean;
var
  Dir, Parent: String;
begin
  Result := True;
  Dir := RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text);
  Parent := ExtractFileDir(Dir);
  if HoldsAryan(Dir) or (Parent = '') or (CompareText(Parent, Dir) = 0) or not HoldsAryan(Parent) then
    exit;
  case MsgBox(Parent + ' already holds Aryan and its library.' + #13#10#13#10 +
      'Yes: update Aryan there and keep the library.' + #13#10 +
      'No: put a new copy, with a new empty library, in ' + Dir + '.',
      mbConfirmation, MB_YESNOCANCEL) of
    IDYES: WizardForm.DirEdit.Text := Parent;
    IDCANCEL: Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not WizardSilent then
    Result := CheckFolderAboveHoldsAryan;
  if (CurPageID = ModePage.ID) and not DirOnCommandLine then
  begin
    // The folder follows the choice until the user types one of their own.
    if IsPortable and (CompareText(WizardForm.DirEdit.Text, InstalledDir) = 0) then
      WizardForm.DirEdit.Text := PortableDir
    else if (not IsPortable) and (CompareText(WizardForm.DirEdit.Text, PortableDir) = 0) then
      WizardForm.DirEdit.Text := InstalledDir;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // The only task is a desktop shortcut, which a portable copy does not make.
  Result := (PageID = wpSelectTasks) and IsPortable;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then
  begin
    // One line each: these labels do not grow, and a longer text is cut off.
    if IsPortable then
    begin
      WizardForm.SelectDirLabel.Caption := 'Choose where to copy Aryan: a USB drive, another drive, or any folder.';
      WizardForm.SelectDirBrowseLabel.Caption := 'If the folder already holds Aryan, only the app is updated and its library is kept.';
    end
    else
    begin
      WizardForm.SelectDirLabel.Caption := InstalledDirLabel;
      WizardForm.SelectDirBrowseLabel.Caption := InstalledBrowseLabel;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if IsPortable then
    Result := 'Portable copy: nothing is written to Windows (no Start menu, no uninstaller).' + NewLine + NewLine +
      MemoDirInfo
  else
    Result := 'Installed for this user.' + NewLine + NewLine + MemoDirInfo;
  if HoldsAryan(WizardDirValue) then
    Result := 'Aryan is already in this folder: the app is updated and its library is kept.' + NewLine + NewLine + Result;
  if (not IsPortable) and (MemoTasksInfo <> '') then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;
