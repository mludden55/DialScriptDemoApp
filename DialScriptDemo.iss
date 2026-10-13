; DialScript DEMO installer - builds Output\DialScript-Demo-Setup.exe
;   1. dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
;   2. Open this file in Inno Setup (free, jrsoftware.org/isinfo.php) and press Ctrl+F9.
; Uses a different AppId and install folder than the paid build, so both can be installed side by side.

#define AppName      "DialScript Demo"
#define AppVersion   "1.0.0"
#define AppPublisher "Lou Companies LLC"
#define AppExe       "DialScript.exe"
#define PublishDir   "bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{B7A1C4E2-3D5F-4F0A-9C61-2E8D7A4B5C90}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\DialScriptDemo
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=DialScript-Demo-Setup
SetupIconFile=icon\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,config.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
