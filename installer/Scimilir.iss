; The Scimilir installer, made with Inno Setup (https://jrsoftware.org/isinfo.php).
; GitHub builds it (see .github/workflows/installer.yml) from the app published
; into ..\publish, and passes the version: ISCC /DAppVersion=0.2.0 Scimilir.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; This id tells Windows that new versions are updates of the same app. Never change it.
AppId={{0F12807F-BE87-4FE4-8C11-976CCF3C043F}
AppName=Scimilir
AppVersion={#AppVersion}
AppVerName=Scimilir {#AppVersion}
AppPublisher=supa-bit
AppPublisherURL=https://github.com/supa-bit/scimilir
; Installs just for you, in your own folders, so it needs no administrator rights.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Scimilir
DisableProgramGroupPage=yes
DisableDirPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Quits a running Scimilir before updating or removing it.
CloseApplications=yes
RestartApplications=no
OutputDir=..\installer-output
OutputBaseFilename=Scimilir-Setup-{#AppVersion}
SetupIconFile=..\Scimilir\icon.ico
UninstallDisplayIcon={app}\Scimilir.exe
UninstallDisplayName=Scimilir
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Put a Scimilir icon on the desktop"; Flags: unchecked
Name: "startup"; Description: "Start Scimilir when Windows starts (so your phone can always reach it)"; Flags: unchecked

[InstallDelete]
; Clear out the old program files on update, so nothing stale is left behind.
; Your settings, chats and models are kept elsewhere and are not touched.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Scimilir"; Filename: "{app}\Scimilir.exe"
Name: "{autodesktop}\Scimilir"; Filename: "{app}\Scimilir.exe"; Tasks: desktopicon
Name: "{userstartup}\Scimilir"; Filename: "{app}\Scimilir.exe"; Parameters: "--background"; Tasks: startup

[Run]
Filename: "{app}\Scimilir.exe"; Description: "Open Scimilir now"; Flags: nowait postinstall skipifsilent

[Messages]
FinishedLabel=Scimilir is installed. You'll find it in the Start menu.%n%nWhile it runs, its icon sits by the clock (you may need to press the small ^ arrow to see it). Click the icon to open Scimilir; right-click it to quit.
