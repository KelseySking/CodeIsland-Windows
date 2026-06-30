#define AppName "CodeIsland-Windows"
#define MainExeName "CodeIsland-Windows.exe"
#define BridgeExeName "codeorbit-bridge.exe"
#define RuntimeHostExeName "codeorbit-host.exe"
#define LegacyBridgeExeName "CodeIsland.Bridge.exe"
#define LegacyRuntimeHostExeName "CodeIsland.RuntimeHost.exe"
#define LegacyCodeOrbitBridgeExeName "CodeOrbit.Bridge.exe"
#define LegacyCodeOrbitRuntimeHostExeName "CodeOrbit.RuntimeHost.exe"
#define AppGuid "B8A0E8F8-36A9-44B9-BD1A-E81EBC8E58C9"
#define AppIdValue "{{B8A0E8F8-36A9-44B9-BD1A-E81EBC8E58C9}"
#define UninstallRegKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppGuid + "}_is1"

#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

#ifndef SourceDir
#define SourceDir "..\.installer-staging"
#endif

#ifndef SetupIconFile
#define SetupIconFile "..\src\CodeIsland.WpfApp\Assets\app.ico"
#endif

[Setup]
AppId={#AppIdValue}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=CodeIsland
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename={#AppName}-Setup-v{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
SetupIconFile={#SetupIconFile}
UninstallDisplayIcon={app}\{#MainExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter={#MainExeName},{#BridgeExeName},{#RuntimeHostExeName},{#LegacyBridgeExeName},{#LegacyRuntimeHostExeName},{#LegacyCodeOrbitBridgeExeName},{#LegacyCodeOrbitRuntimeHostExeName}
UninstallDisplayName={#AppName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
ConfirmUninstall=您确认要卸载 %1 吗？用户设置、本地工具连接配置和日志会保留。

[CustomMessages]
CloseRunningApps=正在关闭正在运行的 CodeIsland...
CloseProcessFailed=无法关闭正在运行的 CodeIsland。请先手动退出 CodeIsland 后重试。
UninstallOldVersion=正在卸载旧版本...
UninstallOldVersionFailed=旧版本卸载失败（退出代码 %1）。请先从 Windows 设置中卸载旧版本后再运行安装程序。
UninstallOldVersionStartFailed=无法启动旧版本卸载程序。请先从 Windows 设置中卸载旧版本后再运行安装程序。
UninstallOldVersionIncomplete=旧版本卸载尚未完成。请稍后重试，或先从 Windows 设置中卸载旧版本。

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\{#MainExeName}"
Type: files; Name: "{app}\{#BridgeExeName}"
Type: files; Name: "{app}\{#RuntimeHostExeName}"
Type: files; Name: "{app}\{#LegacyBridgeExeName}"
Type: files; Name: "{app}\{#LegacyRuntimeHostExeName}"
Type: files; Name: "{app}\{#LegacyCodeOrbitBridgeExeName}"
Type: files; Name: "{app}\{#LegacyCodeOrbitRuntimeHostExeName}"
Type: files; Name: "{app}\CodeIsland-Windows.pdb"
Type: files; Name: "{app}\CodeIsland.Bridge.pdb"
Type: files; Name: "{app}\CodeIsland.Core.pdb"
Type: files; Name: "{app}\*.dll"
Type: filesandordirs; Name: "{app}\Assets"
Type: filesandordirs; Name: "{app}\runtime"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#MainExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#MainExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MainExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function CloseProcess(ImageName: String; var ErrorMessage: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM "' + ImageName + '" /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrorMessage := CustomMessage('CloseProcessFailed');
    Result := False;
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 128) then
  begin
    ErrorMessage := CustomMessage('CloseProcessFailed');
    Result := False;
  end;
end;

function CloseRunningCodeIsland(var ErrorMessage: String): Boolean;
begin
  if WizardForm <> nil then
    WizardForm.StatusLabel.Caption := CustomMessage('CloseRunningApps');

  Result := CloseProcess('{#MainExeName}', ErrorMessage);
  if Result then
    Result := CloseProcess('{#BridgeExeName}', ErrorMessage);
  if Result then
    Result := CloseProcess('{#RuntimeHostExeName}', ErrorMessage);
  if Result then
    Result := CloseProcess('{#LegacyBridgeExeName}', ErrorMessage);
  if Result then
    Result := CloseProcess('{#LegacyRuntimeHostExeName}', ErrorMessage);
  if Result then
    Result := CloseProcess('{#LegacyCodeOrbitBridgeExeName}', ErrorMessage);
  if Result then
    Result := CloseProcess('{#LegacyCodeOrbitRuntimeHostExeName}', ErrorMessage);
end;

function GetInstalledUninstaller(var Uninstaller: String): Boolean;
begin
  Result := RegQueryStringValue(HKCU, '{#UninstallRegKey}', 'UninstallString', Uninstaller);
  if not Result then
    Result := RegQueryStringValue(HKLM, '{#UninstallRegKey}', 'UninstallString', Uninstaller);
end;

function ExtractExecutablePath(CommandLine: String): String;
var
  QuotePosition: Integer;
  SpacePosition: Integer;
begin
  CommandLine := Trim(CommandLine);
  if Copy(CommandLine, 1, 1) = '"' then
  begin
    Delete(CommandLine, 1, 1);
    QuotePosition := Pos('"', CommandLine);
    if QuotePosition > 0 then
      Result := Copy(CommandLine, 1, QuotePosition - 1)
    else
      Result := CommandLine;
    Exit;
  end;

  SpacePosition := Pos(' ', CommandLine);
  if SpacePosition > 0 then
    Result := Copy(CommandLine, 1, SpacePosition - 1)
  else
    Result := CommandLine;
end;

function WaitForFileRemoval(FileName: String; TimeoutMilliseconds: Integer): Boolean;
var
  ElapsedMilliseconds: Integer;
begin
  ElapsedMilliseconds := 0;
  while FileExists(FileName) and (ElapsedMilliseconds < TimeoutMilliseconds) do
  begin
    Sleep(250);
    ElapsedMilliseconds := ElapsedMilliseconds + 250;
  end;

  Result := not FileExists(FileName);
end;

function UninstallPreviousVersion(var ErrorMessage: String): Boolean;
var
  Uninstaller: String;
  UninstallerPath: String;
  ResultCode: Integer;
begin
  Result := True;
  if not GetInstalledUninstaller(Uninstaller) then
    Exit;

  if WizardForm <> nil then
    WizardForm.StatusLabel.Caption := CustomMessage('UninstallOldVersion');

  UninstallerPath := ExtractExecutablePath(Uninstaller);
  if (UninstallerPath = '') or not FileExists(UninstallerPath) then
  begin
    ErrorMessage := CustomMessage('UninstallOldVersionStartFailed');
    Result := False;
    Exit;
  end;

  if not Exec(UninstallerPath, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrorMessage := CustomMessage('UninstallOldVersionStartFailed');
    Result := False;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorMessage := FmtMessage(CustomMessage('UninstallOldVersionFailed'), [IntToStr(ResultCode)]);
    Result := False;
    Exit;
  end;

  if not WaitForFileRemoval(UninstallerPath, 10000) then
  begin
    ErrorMessage := CustomMessage('UninstallOldVersionIncomplete');
    Result := False;
    Exit;
  end;

  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorMessage: String;
begin
  Result := '';
  NeedsRestart := False;

  if not CloseRunningCodeIsland(ErrorMessage) then
  begin
    Result := ErrorMessage;
    Exit;
  end;

  if not UninstallPreviousVersion(ErrorMessage) then
  begin
    Result := ErrorMessage;
    Exit;
  end;
end;
