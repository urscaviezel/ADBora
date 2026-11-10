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
; The language chosen here (preselected: Windows display language) also becomes ADBora's language at
; the first start ({app}\ADBora.ini). Optional languages only if this Inno Setup has the file.
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
#if FileExists(CompilerPath + "Languages\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#elif FileExists(CompilerPath + "Languages\Unofficial\ChineseSimplified.isl")
Name: "chinese"; MessagesFile: "compiler:Languages\Unofficial\ChineseSimplified.isl"
#endif

[CustomMessages]
; ADBora language code per setup language (unprefixed = fallback)
LangCode=en
german.LangCode=de
french.LangCode=fr
italian.LangCode=it
spanish.LangCode=es
portuguese.LangCode=pt
russian.LangCode=ru
#if FileExists(CompilerPath + "Languages\Korean.isl")
korean.LangCode=ko
#endif
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl") || FileExists(CompilerPath + "Languages\Unofficial\ChineseSimplified.isl")
chinese.LangCode=zh-cn
#endif
DeleteUserData=Do you also want to delete the ADBora user data?%n%nSettings, saved commands and metadata cache in:%n%1%n%nYour APK backups will NOT be deleted.
german.DeleteUserData=Sollen auch die Nutzerdaten von ADBora gelöscht werden?%n%nEinstellungen, gespeicherte Befehle und Metadaten-Cache in:%n%1%n%nIhre APK-Backups werden NICHT gelöscht.
french.DeleteUserData=Supprimer aussi les données utilisateur d'ADBora ?%n%nParamètres, commandes enregistrées et cache des métadonnées dans :%n%1%n%nVos sauvegardes APK ne seront PAS supprimées.
italian.DeleteUserData=Eliminare anche i dati utente di ADBora?%n%nImpostazioni, comandi salvati e cache dei metadati in:%n%1%n%nI tuoi backup APK NON verranno eliminati.
spanish.DeleteUserData=¿Eliminar también los datos de usuario de ADBora?%n%nAjustes, comandos guardados y caché de metadatos en:%n%1%n%nTus copias de APK NO se eliminarán.
portuguese.DeleteUserData=Excluir também os dados de usuário do ADBora?%n%nConfigurações, comandos salvos e cache de metadados em:%n%1%n%nSeus backups de APK NÃO serão excluídos.
russian.DeleteUserData=Удалить также пользовательские данные ADBora?%n%nНастройки, сохранённые команды и кэш метаданных в:%n%1%n%nВаши бэкапы APK НЕ будут удалены.
#if FileExists(CompilerPath + "Languages\Korean.isl")
korean.DeleteUserData=ADBora 사용자 데이터도 삭제하시겠습니까?%n%n설정, 저장된 명령 및 메타데이터 캐시 위치:%n%1%n%nAPK 백업은 삭제되지 않습니다.
#endif
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl") || FileExists(CompilerPath + "Languages\Unofficial\ChineseSimplified.isl")
chinese.DeleteUserData=是否同时删除 ADBora 的用户数据？%n%n设置、已保存的命令和元数据缓存位于：%n%1%n%n您的 APK 备份不会被删除。
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\ADBora.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\adb\*"; DestDir: "{app}\adb"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[INI]
; Language chosen in the setup – used by ADBora only at its first start
Filename: "{app}\ADBora.ini"; Section: "Setup"; Key: "Language"; String: "{cm:LangCode}"; Flags: uninsdeletesection

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
