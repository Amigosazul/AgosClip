; AgosClip installer. Build with tools\build.ps1 (publishes the app first).
#define AppName "AgosClip"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
#ifdef TestBuild
; install round-trip test: separate identity, never touches a real installation
AppId={{0B7F3C2E-91A4-4D6B-8E25-7C1D9A3F5B60}
AppName={#AppName} Test
OutputBaseFilename={#AppName}-SetupTest-{#AppVersion}
#else
AppId={{6E7C1B9A-3F2D-4C58-9A1E-5D2B8F4C7A31}
AppName={#AppName}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
#endif
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
; Upgrades reuse the previous folder: Inno's uninstall record first (UsePreviousAppDir),
; then the folder the app itself recorded in HKCU\Software\AgosClip.
DefaultDirName={code:GetDefaultDir}
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
SetupIconFile=..\src\Assets\AgosClip.ico
UninstallDisplayIcon={app}\AgosClip.exe
#ifdef TestBuild
UninstallDisplayName={#AppName} 安装测试
#else
UninstallDisplayName={#AppName} 剪贴板
#endif
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
ShowLanguageDialog=no

[Languages]
Name: "zh"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifdef TestBuild
Name: "{autoprograms}\{#AppName} Test"; Filename: "{app}\AgosClip.exe"
#else
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\AgosClip.exe"
#endif
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\AgosClip.exe"; Tasks: desktopicon

#ifndef TestBuild
[Registry]
; the app writes its own autostart value; make sure uninstall removes it
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "AgosClip"; Flags: uninsdeletevalue dontcreatekey

[Registry]
Root: HKCU; Subkey: "Software\AgosClip"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey

#endif

[Run]
Filename: "{app}\AgosClip.exe"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent
; online upgrade runs the installer with /SILENT /RESTARTAPP: start the new version afterwards
Filename: "{app}\AgosClip.exe"; Parameters: "--updated"; Flags: nowait; Check: RestartRequested

[Code]
function RestartRequested(): Boolean;
var i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/RESTARTAPP') = 0 then Result := True;
end;

function GetDefaultDir(Param: String): String;
var Dir: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\AgosClip');
  if RegQueryStringValue(HKCU, 'Software\AgosClip', 'InstallDir', Dir) then
    if (Dir <> '') and FileExists(AddBackslash(Dir) + 'AgosClip.exe') then Result := RemoveBackslash(Dir);
end;

procedure StopApp();
var Code: Integer;
begin
#ifdef TestBuild
  exit;
#endif
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im AgosClip.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();  // upgrade in place; the hook is released when the process ends
  Result := '';
end;

var CustomData: String;

function InitializeUninstall(): Boolean;
begin
  // read before [Registry] cleanup removes HKCU\Software\AgosClip
  if not RegQueryStringValue(HKCU, 'Software\AgosClip', 'DataDir', CustomData) then CustomData := '';
  StopApp();  // ending the process removes the Win+V hook, so the system Win+V works again
  Result := True;
end;

procedure DeleteCustomData(Dir: String);
begin
  // only AgosClip's own files: a custom folder may sit next to other things
  DeleteFile(AddBackslash(Dir) + 'history.db');
  DeleteFile(AddBackslash(Dir) + 'history.db-wal');
  DeleteFile(AddBackslash(Dir) + 'history.db-shm');
  DelTree(AddBackslash(Dir) + 'images', True, True, True);
  RemoveDir(Dir);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Data, Where: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
#ifdef TestBuild
    exit;
#endif
    Data := ExpandConstant('{userappdata}\AgosClip');
    if (CustomData <> '') and (CompareText(RemoveBackslash(CustomData), Data) = 0) then CustomData := '';
    if CustomData <> '' then Where := CustomData + #13#10 + Data else Where := Data;
    if (DirExists(Data) or ((CustomData <> '') and DirExists(CustomData))) and not UninstallSilent() then
      if MsgBox('要同时删除剪贴板历史记录吗？' + #13#10#13#10 + '位置：' + #13#10 + Where + #13#10#13#10 +
                '选“否”会保留记录，以后重新安装还能看到。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        if (CustomData <> '') and DirExists(CustomData) then DeleteCustomData(CustomData);
        DelTree(Data, True, True, True);
      end;
  end;
end;
