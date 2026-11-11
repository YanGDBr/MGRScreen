#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId=MGRScreen.Windows
AppName=MGRScreen
AppVersion={#AppVersion}
AppPublisher=YanGDBr
AppPublisherURL=https://github.com/YanGDBr/MGRScreen
AppSupportURL=https://github.com/YanGDBr/MGRScreen/issues
AppUpdatesURL=https://github.com/YanGDBr/MGRScreen/releases/latest
DefaultDirName={localappdata}\Programs\MGRScreen
DefaultGroupName=MGRScreen
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
CloseApplicationsFilter=MGRScreen.exe,ffmpeg.exe,*.dll
RestartApplications=no
UninstallDisplayName=MGRScreen
UninstallDisplayIcon={app}\MGRScreen.exe
SetupIconFile=..\..\src\MGRScreen.App\Assets\mgrscreen.ico
OutputDir={#OutputDir}
OutputBaseFilename=MGRScreen-{#AppVersion}-windows-x64-setup
VersionInfoVersion={#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MGRScreen"; Filename: "{app}\MGRScreen.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\MGRScreen"; Filename: "{app}\MGRScreen.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MGRScreen.exe"; Description: "{cm:LaunchProgram,MGRScreen}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
