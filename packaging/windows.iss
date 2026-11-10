#define SourceDir GetEnv('HAMRESAN_PUBLISH')
#define OutputDir GetEnv('HAMRESAN_INSTALLER_OUTPUT')
[Setup]
AppId={{073FD04E-66FB-40A9-84E4-F864F0907C72}
AppName=HamResan Experimental
AppVersion=0.1.0
DefaultDirName={autopf}\HamResan
DefaultGroupName=HamResan
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=HamResan-Windows-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\HamResan"; Filename: "{app}\HamResan.App.exe"
[Run]
Filename: "{app}\HamResan.App.exe"; Description: "Launch HamResan"; Flags: nowait postinstall skipifsilent
