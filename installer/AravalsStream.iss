; Inno Setup Script for Aravals Stream
#define MyAppName "Aravals Stream"
#ifndef MyAppVersion
  #error Build with /DMyAppVersion from Directory.Build.props.
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\publish"
#endif
#define MyAppPublisher "Aravals"
#define MyAppExeName "AravalsStream.App.exe"
#define MyAppAssocName MyAppName + " Session"
#define MyAppAssocExt ".aravals"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

[Setup]
AppId={{5E97D4B6-7C34-4F1E-9C3E-A53E0F44781A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile=..\src\AravalsStream.App\Assets\Brand\Aravals Stream.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
DefaultDirName={autopf}\{#MyAppName}
ArchitecturesInstallIn64BitMode=x64compatible
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=AravalsStream-Setup-{#MyAppVersion}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenu"; Description: "Create a Start Menu shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; All published application files
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Note: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenu
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; Tasks: startmenu
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked
