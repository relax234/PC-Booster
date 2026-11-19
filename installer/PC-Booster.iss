#define MyAppName "PC Booster"
#ifndef AppVersion
  #define AppVersion "0.4.4"
#endif
#define MyAppPublisher "PC Booster"
#define MyAppExeName "PC Booster.exe"

[Setup]
AppId={{8B6C1A25-52BD-4F62-9F23-71A4B52D1C44}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PC Booster
DefaultGroupName=PC Booster
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=.
OutputBaseFilename=PC-Booster-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: checkedonce

[Files]
Source: "PC-Booster.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "xdelta3.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PC Booster"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\PC Booster"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить PC Booster"; Flags: postinstall nowait skipifsilent
