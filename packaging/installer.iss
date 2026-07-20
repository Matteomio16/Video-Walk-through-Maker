; Inno Setup script for the Video Walk-through Maker installer.
; Compiled automatically by packaging\build-release.ps1 when Inno Setup 6 is
; installed (winget install JRSoftware.InnoSetup), or manually:
;   ISCC.exe /DAppVersion=1.2.3 packaging\installer.iss
;
; Produces a single setup exe. Installs per-user (no admin rights, no UAC
; prompt, no console window at any point), creates a Start Menu shortcut,
; and can launch the app when the wizard finishes.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef DistDir
  #define DistDir "..\dist\VideoWalkthroughMaker"
#endif

[Setup]
AppId={{9D3B1C86-4A45-4C0F-9C9E-51D7C2A8B4E6}
AppName=Video Walk-through Maker
AppVersion={#AppVersion}
DefaultDirName={autopf}\Video Walk-through Maker
DefaultGroupName=Video Walk-through Maker
; per-user install: no admin rights or UAC prompt; {autopf} resolves to
; %LocalAppData%\Programs
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
OutputDir=..\dist
OutputBaseFilename=VideoWalkthroughMaker-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\VideoWalkthroughMaker.exe
CloseApplications=yes

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"

[Icons]
Name: "{group}\Video Walk-through Maker"; Filename: "{app}\VideoWalkthroughMaker.exe"
Name: "{autodesktop}\Video Walk-through Maker"; Filename: "{app}\VideoWalkthroughMaker.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VideoWalkthroughMaker.exe"; Description: "Launch Video Walk-through Maker"; Flags: nowait postinstall skipifsilent
