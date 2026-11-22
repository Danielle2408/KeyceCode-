#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef InstallerDeps
  #define InstallerDeps "..\artifacts\installer-deps"
#endif

[Setup]
AppId={{A31F56C4-7DC6-46A3-889C-7354A2B1E806}
AppName=KeyceCode
AppVersion=0.1.0
AppPublisher=KeyceCode
DefaultDirName={localappdata}\Programs\KeyceCode
DefaultGroupName=KeyceCode
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=KeyceCode-Setup-0.1.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\KeyceCode.exe
SetupLogging=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#InstallerDeps}\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\KeyceCode"; Filename: "{app}\KeyceCode.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\KeyceCode"; Filename: "{app}\KeyceCode.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installation du composant WebView2 requis…"; Flags: waituntilterminated
Filename: "{app}\KeyceCode.exe"; Description: "Lancer KeyceCode"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
