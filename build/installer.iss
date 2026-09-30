; ADBora – Inno Setup 6 script (called by BUILD.ps1)
; Installed mode: settings and caches in %LOCALAPPDATA%\ADBora.
; The uninstaller asks whether these user data should be deleted as well.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{E74C5A74-274D-4472-9BC9-CD41228F092A}
AppName=ADBora
AppVersion={#AppVersion}
AppVerName=ADBora {#AppVersion}
AppPublisher=urscaviezel
AppPublisherURL=https://github.com/urscaviezel
DefaultDirName={autopf}\ADBora
DefaultGroupName=ADBora
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ADBora-Setup-{#AppVersion}
SetupIconFile=..\src\AdbTool\ADBora.ico
UninstallDisplayIcon={app}\ADBora.exe
UninstallDisplayName=ADBora
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UsedUserAreasWarning=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.DeleteUserData=Sollen auch die Nutzerdaten von ADBora gelöscht werden?%n%nEinstellungen, gespeicherte Befehle und Metadaten-Cache in:%n%1%n%nIhre APK-Backups werden NICHT gelöscht.
english.DeleteUserData=Do you also want to delete the ADBora user data?%n%nSettings, saved commands and metadata cache in:%n%1%n%nYour APK backups will NOT be deleted.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\ADBora.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\adb\*"; DestDir: "{app}\adb"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\ADBora"; Filename: "{app}\ADBora.exe"
Name: "{autodesktop}\ADBora"; Filename: "{app}\ADBora.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ADBora.exe"; Description: "{cm:LaunchProgram,ADBora}"; Flags: nowait postinstall skipifsilent
; Automatic update from within ADBora (/RELAUNCH=1): start the new version again
Filename: "{app}\ADBora.exe"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

[Code]
function RelaunchAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\ADBora');
    if DirExists(DataDir) then
      if SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteUserData'), [DataDir]),
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
