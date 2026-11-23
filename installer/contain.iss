#ifndef SourcePayloadDir
  #error "SourcePayloadDir must be provided via /DSourcePayloadDir=..."
#endif
#ifndef AppVersion
  #define AppVersion "0.6.0-beta.4"
#endif
#ifndef VersionInfoVersion
  #define VersionInfoVersion "0.6.0.4"
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "Contain-Setup-x64"
#endif

#define MyAppName "Contain"
#define MyAppId "{{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}"
#define MyAppExeName "Contain.Viewer.exe"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher=LuckyLivio
AppPublisherURL=https://github.com/LuckyLivio/Contain
AppSupportURL=https://github.com/LuckyLivio/Contain
AppUpdatesURL=https://github.com/LuckyLivio/Contain
DefaultDirName={localappdata}\Programs\Contain
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#VersionInfoVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=Contain Windows x64 Per-User Installer ({#AppVersion})
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourcePayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "记录安装行为、检查项目环境与验证交付包"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "记录安装行为、检查项目环境与验证交付包"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  CONTAIN_GENERIC_READ = $80000000;
  CONTAIN_GENERIC_WRITE = $40000000;
  CONTAIN_OPEN_EXISTING = 3;
  CONTAIN_FILE_ATTR_NORMAL = $00000080;
  CONTAIN_INVALID_HANDLE = -1;
  CONTAIN_ERR_SHARING_VIOLATION = 32;

function FindWindowEx(Parent, ChildAfter: HWND; ClassName, WindowTitle: String): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function GetWindowText(Wnd: HWND; Buffer: String; MaxCount: Integer): Integer;
  external 'GetWindowTextW@user32.dll stdcall';
function CreateFileCheck(FileName: String; DesiredAccess, ShareMode: Cardinal; SecurityAttributes: Integer; CreationDisposition, FlagsAndAttributes: Cardinal; TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandleCheck(ObjectHandle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function GetLastErrorCheck(): Cardinal;
  external 'GetLastError@kernel32.dll stdcall';

function HasRunningContainWindow(): Boolean;
var
  Wnd: HWND;
  Buf: String;
  Len: Integer;
  Title: String;
begin
  Result := False;
  Wnd := FindWindowEx(0, 0, '', '');
  while Wnd <> 0 do
  begin
    SetLength(Buf, 256);
    Len := GetWindowText(Wnd, Buf, 255);
    if Len > 0 then
    begin
      Title := Copy(Buf, 1, Len);
      if Pos('Contain · ', Title) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
    Wnd := FindWindowEx(0, Wnd, '', '');
  end;
end;

function IsExecutableInUse(const FilePath: String): Boolean;
var
  Handle: THandle;
  Err: Cardinal;
begin
  Result := False;
  if not FileExists(FilePath) then
    Exit;

  Handle := CreateFileCheck(
    FilePath,
    CONTAIN_GENERIC_READ or CONTAIN_GENERIC_WRITE,
    0,
    0,
    CONTAIN_OPEN_EXISTING,
    CONTAIN_FILE_ATTR_NORMAL,
    0);

  if Handle = THandle(CONTAIN_INVALID_HANDLE) then
  begin
    Err := GetLastErrorCheck();
    if Err = CONTAIN_ERR_SHARING_VIOLATION then
      Result := True;
  end
  else
  begin
    CloseHandleCheck(Handle);
  end;
end;

function IsAnyContainInstanceActive(const TargetDir: String): Boolean;
begin
  Result :=
    HasRunningContainWindow()
    or IsExecutableInUse(AddBackslash(TargetDir) + 'Contain.Viewer.exe')
    or IsExecutableInUse(AddBackslash(TargetDir) + 'contain.exe')
    or IsExecutableInUse(AddBackslash(TargetDir) + 'contain-workflow.exe');
end;

function EnsureContainClosedBeforeProceeding(const TargetDir: String; const IsUninstall: Boolean): Boolean;
var
  ActionWord: String;
  PromptText: String;
begin
  if IsUninstall then
    ActionWord := '卸载'
  else
    ActionWord := '安装或更新';

  while IsAnyContainInstanceActive(TargetDir) do
  begin
    if WizardSilent() then
    begin
      Log('Refusing silent ' + ActionWord + ' while Contain is running.');
      Result := False;
      Exit;
    end;

    PromptText :=
      '检测到 Contain 当前正在运行。' + #13#10#13#10 +
      '若正在记录安装过程或保存证据，请先回到 Contain 窗口等待保存结束并正常关闭程序，以免中断正在保存的记录。' + #13#10#13#10 +
      '正常关闭 Contain 后点击“确定”继续' + ActionWord + '，或点击“取消”退出。';

    if MsgBox(PromptText, mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;

  Result := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := EnsureContainClosedBeforeProceeding(ExpandConstant('{localappdata}\Programs\Contain'), False);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  if not EnsureContainClosedBeforeProceeding(WizardDirValue(), False) then
    Result := '请先等待正在保存的记录完成并正常关闭 Contain 后再继续安装。'
  else
    Result := '';
end;

function InitializeUninstall(): Boolean;
var
  PromptText: String;
begin
  while IsAnyContainInstanceActive(ExpandConstant('{app}')) do
  begin
    if UninstallSilent() then
    begin
      Log('Refusing silent uninstall while Contain is running.');
      Result := False;
      Exit;
    end;

    PromptText :=
      '检测到 Contain 当前正在运行。' + #13#10#13#10 +
      '请先等待正在保存的记录完成并正常关闭 Contain 窗口，再点击“确定”继续卸载，或点击“取消”退出。';

    if MsgBox(PromptText, mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;

  Result := True;
end;
