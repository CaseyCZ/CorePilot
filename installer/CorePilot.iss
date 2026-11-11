#define AppName "CorePilot"
#define AppVersion "2.2.0"
#define AppPublisher "CaseyCZ"

[Setup]
AppId={{DC20C232-E3E1-4773-91CA-5C2C873F60B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/CaseyCZ/CorePilot
AppSupportURL=https://github.com/CaseyCZ/CorePilot/issues
DefaultDirName={localappdata}\Programs\CorePilot
DefaultGroupName=CorePilot
DisableProgramGroupPage=yes
OutputDir=..\out\release
OutputBaseFilename=CorePilot-Setup
SetupIconFile=..\Assets\CorePilot.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\CorePilot.exe
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\CorePilot"; Filename: "{app}\CorePilot.exe"
Name: "{autodesktop}\CorePilot"; Filename: "{app}\CorePilot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CorePilot.exe"; Description: "{cm:LaunchProgram,CorePilot}"; Flags: nowait postinstall skipifsilent
