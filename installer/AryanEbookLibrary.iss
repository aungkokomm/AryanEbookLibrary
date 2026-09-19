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
Name: "{group}\{#AppName}";           Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

; NO [UninstallDelete], on purpose. The user's library (AryanLibrary-Data) lives inside {app}.
; Inno removes only the files it installed, and that folder is excluded from the payload, so an
; uninstall leaves the library (favorites, notes, reading progress, covers) in place for a
; reinstall. An UninstallDelete of {app} would erase it.
