; Inno Setup script for PersonalLauncher (Per-User Installer)
; Preserves %LOCALAPPDATA%\PersonalLauncher on upgrade and NEVER deletes user games on uninstall.

#define MyAppName "PersonalLauncher"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "CuongAnime"
#define MyAppExeName "PersonalLauncher.exe"

[Setup]
AppId={{8F54A3E9-72C3-4C5E-A285-C942F8B40119}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=PersonalLauncher-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\PersonalLauncher.App\Assets\logo.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,libvlc\win-x86\*,libvlc\win-arm64\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\logo.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\logo.ico"; Tasks: desktopicon

[UninstallDelete]
; Never delete user game folders or %LOCALAPPDATA%\PersonalLauncher automatically.

[Code]
// Clean up ONLY PersonalLauncher's own HKCU Run startup registration on uninstall
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PersonalLauncher');
  end;
end;

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
