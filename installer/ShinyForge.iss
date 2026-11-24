; Inno Setup script for ShinyForge (installs per-user, no admin rights needed).
; One file. The Pokedex tracker spreadsheets are no longer downloaded: DexTracker (bundled) is the Living Dex / Pokedex,
; and trackers a user already has keep being updated by ShinyForge.
#define AppName "ShinyForge"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define Publish "..\dist\publish"

[Setup]
AppId={{6F3C2A6E-9B1D-4E2B-8C59-5F1D7B0A2C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ShinyForge
AppPublisherURL=https://github.com/Domsanna/ShinyForge
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
CloseApplications=yes
RestartApplications=yes
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ShinyForge-Setup
SetupIconFile=..\ShinyForge\Assets\ShinyForge.ico
UninstallDisplayIcon={app}\ShinyForge.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#Publish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.flag"

[Dirs]
; Never removed on uninstall (the user's files): the Backup Tools folder.
Name: "{userdocs}\ShinyForge\Backup Tools"; Flags: uninsneveruninstall

[Messages]
FinishedLabel=ShinyForge is installed.%n%nYour Living Dex and Pokedex are in DexTracker (the DexTracker button in ShinyForge). Pokedex tracker spreadsheets you already have keep being updated.%n%nBackup Tools folder: Documents\ShinyForge\Backup Tools%nIf ShinyForge can't find a program it needs (FFmpeg, PKHeX, SysBot, 3DS Capture, Python...) on this PC, it looks in that folder.

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\ShinyForge.exe"
Name: "{group}\ShinyForge User Guide"; Filename: "{app}\ShinyForge User Guide.html"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{group}\ShinyForge Backup Tools folder"; Filename: "{userdocs}\ShinyForge\Backup Tools"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\ShinyForge.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ShinyForge.exe"; Description: "Launch ShinyForge"; Flags: nowait postinstall skipifsilent
