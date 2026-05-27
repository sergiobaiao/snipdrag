#define AppName "SnipDrag"
#ifndef AppVersion
#define AppVersion "0.0.0"
#endif
#ifndef SourceDir
#define SourceDir "..\bin\Release\net10.0-windows\win-x64\publish"
#endif
#ifndef OutputDir
#define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{6D89C04D-EB5A-45C5-B6C5-6C632F74E58E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=SnipDrag
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=SnipDrag-Setup-{#AppVersion}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\SnipDrag.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SnipDrag"; Filename: "{app}\SnipDrag.exe"
Name: "{autodesktop}\SnipDrag"; Filename: "{app}\SnipDrag.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SnipDrag.exe"; Description: "Iniciar SnipDrag"; Flags: nowait postinstall skipifsilent
