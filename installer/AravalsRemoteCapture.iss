; Inno Setup script for the standalone Aravals Remote Capture Agent.
#define MyAppName "Aravals Remote Capture"
#ifndef MyAppVersion
  #error Build with /DMyAppVersion from Directory.Build.props.
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\publish-agent"
#endif
#define MyAppExeName "AravalsRemoteCapture.exe"

[Setup]
AppId={{2A10AE8E-2B54-4B8E-A8D4-52C6DF8A079A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Aravals
SetupIconFile=..\src\AravalsStream.App\Assets\Brand\Aravals Stream.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
DefaultDirName={localappdata}\Programs\Aravals Remote Capture
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=AravalsRemoteCapture-Setup-{#MyAppVersion}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Aravals Remote Capture"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Aravals Remote Capture"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Aravals Remote Capture"; Flags: nowait postinstall skipifsilent unchecked
