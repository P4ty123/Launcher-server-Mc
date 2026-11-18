; Installer di MineServer Manager (Inno Setup 6)
; Compilato automaticamente da GitHub Actions:  ISCC /DMyAppVersion=1.2.3 installer\setup.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "MineServer Manager"
#define MyAppExe "MineServerManager.exe"
#define MyAppUrl "https://github.com/P4ty123/Launcher-server-Mc"

[Setup]
AppId={{6B0E7C41-3F2A-4E8D-9C55-2D7A1B9E4F10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=P4ty123
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
; Cartella semplice e scrivibile: il programma salva lì server, mondi e backup
DefaultDirName={sd}\MineServerManager
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=MineServerManager-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
ShowLanguageDialog=no

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; Gli utenti possono scrivere nella cartella: così gli aggiornamenti non chiedono permessi
Name: "{app}"; Permissions: users-modify

[Files]
Source: "..\MineServerManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LEGGIMI.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; Dopo un aggiornamento automatico (installazione silenziosa) riapre il programma
Filename: "{app}\{#MyAppExe}"; Parameters: "--wait --updated"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""MineServerManager"" /F"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v MineServerManager /f"; Flags: runhidden; RunOnceId: "DelRun"

[UninstallDelete]
Type: files; Name: "{app}\MineServerManager.old.exe"

[Code]
// Aspetta che il programma si sia chiuso (serve durante gli aggiornamenti automatici)
function InitializeSetup(): Boolean;
var
  i: Integer;
begin
  i := 0;
  while CheckForMutexes('MineServerManager_SingleInstance') and (i < 40) do
  begin
    Sleep(500);
    i := i + 1;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('Il programma è stato rimosso.' + #13#10 + #13#10 +
           'I server, i mondi e i backup sono stati LASCIATI nella cartella:' + #13#10 +
           ExpandConstant('{app}') + #13#10 + #13#10 +
           'Se non ti servono più puoi cancellarla a mano.', mbInformation, MB_OK);
end;
