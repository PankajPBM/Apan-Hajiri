#define MyAppId "{{AF12B76A-7F83-4F7F-8CA6-8C32A67D39C1}"
#define MyAppName "E-Attendance"
#define MyAppPublisher "E-Attendance Team"
#define MyAppVersion "1.0.0"

#ifndef BuildMode
  #define BuildMode "Offline"
#endif

#ifndef AppExePath
  #if FileExists("..\artifacts\single-exe\E.Attendance.Web\E.Attendance.Web.exe")
    #define AppExePath "..\artifacts\single-exe\E.Attendance.Web\E.Attendance.Web.exe"
    #define BuildMode "Offline"
  #elif FileExists("..\artifacts\single-exe-min\E.Attendance.Web\E.Attendance.Web.exe")
    #define AppExePath "..\artifacts\single-exe-min\E.Attendance.Web\E.Attendance.Web.exe"
    #define BuildMode "Min"
  #elif FileExists("..\E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.exe")
    #define AppExePath "..\E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.exe"
    #define BuildMode "Offline"
  #elif FileExists("..\E.Attendance.Web\bin\Release\net10.0\win-x64\E.Attendance.Web.exe")
    #define AppExePath "..\E.Attendance.Web\bin\Release\net10.0\win-x64\E.Attendance.Web.exe"
    #define BuildMode "Offline"
  #elif FileExists("..\E.Attendance.Web\bin\Debug\net10.0\E.Attendance.Web.exe")
    #define AppExePath "..\E.Attendance.Web\bin\Debug\net10.0\E.Attendance.Web.exe"
    #define BuildMode "Offline"
  #else
    #error "AppExePath could not be auto-detected. Build the app EXE first or pass /DAppExePath=..."
  #endif
#endif
#define AppContentDir AddBackslash(ExtractFilePath(AppExePath))

#ifndef ControllerExePath
  #if FileExists("..\artifacts\single-exe\E.Attendance.Controller\E.Attendance.exe")
    #define ControllerExePath "..\artifacts\single-exe\E.Attendance.Controller\E.Attendance.exe"
    #define ControllerBinDir "..\artifacts\single-exe\E.Attendance.Controller"
  #elif FileExists("..\artifacts\single-exe\E.Attendance.Controller.ExeOnly\E.Attendance.exe")
    #define ControllerExePath "..\artifacts\single-exe\E.Attendance.Controller.ExeOnly\E.Attendance.exe"
    #define ControllerBinDir "..\artifacts\single-exe\E.Attendance.Controller.ExeOnly"
  #elif FileExists("..\E.Attendance\bin\Debug\net10.0-windows\E.Attendance.exe")
    #define ControllerExePath "..\E.Attendance\bin\Debug\net10.0-windows\E.Attendance.exe"
    #define ControllerBinDir "..\E.Attendance\bin\Debug\net10.0-windows"
  #elif FileExists("..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.exe")
    #define ControllerExePath "..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.exe"
    #define ControllerBinDir "..\E.Attendance\bin\Release\net10.0-windows"
  #endif
#endif

#ifndef SetupIconPath
  #if FileExists("..\icon\E_Att.ico")
    #define SetupIconPath "..\icon\E_Att.ico"
  #elif FileExists("..\branding\E_Att.ico")
    #define SetupIconPath "..\branding\E_Att.ico"
  #else
    #error "SetupIconPath define is required. Pass /DSetupIconPath=... or place E_Att.ico in icon folder."
  #endif
#endif

#ifndef OutputDirPath
  #define OutputDirPath "..\installer\exe"
#endif

#ifndef PostgresInstallerName
  #define PostgresInstallerName "postgresql-18.3-3.exe"
#endif

#ifndef PostgresInstallerPath
  #if FileExists("..\installer\postgresql-18.3-3.exe")
    #define PostgresInstallerPath "..\installer\postgresql-18.3-3.exe"
  #elif FileExists("..\installer\postgresql-18.3-1-windows-x64.exe")
    #define PostgresInstallerPath "..\installer\postgresql-18.3-1-windows-x64.exe"
  #elif FileExists("D:\Pankaj\Project\E-Attendance\installer\postgresql-18.3-3.exe")
    #define PostgresInstallerPath "D:\Pankaj\Project\E-Attendance\installer\postgresql-18.3-3.exe"
  #else
    #define PostgresInstallerPath ""
  #endif
#endif

#if defined(PostgresInstallerPath) && PostgresInstallerPath != ""
  #if !FileExists(PostgresInstallerPath)
    #error "PostgresInstallerPath does not exist. PostgreSQL installer required for automated setup."
  #endif
#endif

#define PgServiceName "E_Attendance_PostgreSQL"
#define PgPort "1277"
#define PgPassword "PB@14891277"
#define PgDatabase "E_Attendace"
#define PgSchema "E_Attendace"
#define PgInstallSubdir "PostgreSQL\\15"
#define PgDataSubdir "PostgreSQL\\15\\data"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\E-Attendance
DefaultGroupName=E-Attendance
DisableProgramGroupPage=yes
OutputDir={#OutputDirPath}
OutputBaseFilename=E-Attendance_{#BuildMode}_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#SetupIconPath}
UninstallDisplayIcon={app}\E.Attendance.exe
CloseApplications=force
CloseApplicationsFilter=E.Attendance.exe,E.Attendance.Web.exe
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create desktop icons"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Start E_Attendace when Windows starts"

[Files]
; Web app and dependencies
Source: "{#AppContentDir}*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Service Controller application files - include ALL files from the published output
#if defined(ControllerBinDir)
Source: "{#ControllerBinDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
; Fallback to bin/Release ONLY if artifacts are missing (not recommended for production)
Source: "..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.deps.json"; DestDir: "{app}"; Flags: ignoreversion
#endif

#if defined(PostgresInstallerPath)
Source: "{#PostgresInstallerPath}"; DestDir: "{tmp}"; DestName: "{#PostgresInstallerName}"; Flags: deleteafterinstall ignoreversion
#endif

[Icons]
#if defined(ControllerExePath)
Name: "{autodesktop}\E_Att_Service_Controller"; Filename: "{app}\E.Attendance.exe"; Tasks: desktopicon
Name: "{autodesktop}\E_Attendace"; Filename: "http://127.0.0.1:8001/"; Tasks: desktopicon
#else
Name: "{autodesktop}\E_Attendace"; Filename: "http://127.0.0.1:8001/"; Tasks: desktopicon
#endif

[Registry]
#if defined(ControllerExePath)
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "E_Attendace Service"; ValueData: """{app}\E.Attendance.exe"""; Tasks: autostart; Flags: uninsdeletevalue
#else
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "E_Attendace Web"; ValueData: """{app}\E.Attendance.Web.exe"""; Tasks: autostart; Flags: uninsdeletevalue
#endif

[Run]
; Install PostgreSQL if needed (BEFORE E-Attendance setup)
#if defined(PostgresInstallerPath)
Filename: "{tmp}\{#PostgresInstallerName}"; Parameters: "--mode unattended --unattendedmodeui minimal --prefix ""C:\PostgreSQL"" --datadir ""C:\PostgreSQL\data"" --serverport 5432 --superaccount ""postgres"" --superpassword ""PB@14891277"" --serviceaccount ""postgres"" --servicepassword ""PB@14891277"" --servicename ""postgresql"" --enable-components ""server,pgAdmin,stackbuilder,commandlinetools"""; StatusMsg: "Installing PostgreSQL..."; Flags: waituntilterminated
#endif

; Launch E-Attendance Service Controller after installation
Filename: "{app}\E.Attendance.exe"; Description: "Launch E-Attendance Service"; Flags: nowait postinstall skipifsilent

[Code]
var
  InstallModePage: TWizardPage;
  RbUpdateInstall: TRadioButton;
  RbFreshInstall: TRadioButton;
  PortSelectionPage: TWizardPage;
  ApiPortLabel: TNewStaticText;
  ApiPortEdit: TNewEdit;
  WebPortLabel: TNewStaticText;
  WebPortEdit: TNewEdit;
  SelectedApiPort: string;
  SelectedWebPort: string;
  FreshInstallRequested: Boolean;
  PostgresModePage: TWizardPage;
  RbPgKeepInstall: TRadioButton;
  RbPgFreshInstall: TRadioButton;
  ForceFreshPostgreSqlInstall: Boolean;
  InstallProgressPage: TOutputProgressWizardPage;
  AutoStartTaskPreset: Boolean;
  PostgreSQLDetected: Boolean;
  DatabaseExists: Boolean;
  SkipCustomPages: Boolean;

function IsExistingInstallAtPath(const InstallDir: string): Boolean;
var
  BasePath: string;
begin
  BasePath := AddBackslash(InstallDir);
  Result :=
    FileExists(BasePath + 'E.Attendance.exe') or
    FileExists(BasePath + 'E.Attendance.Web.exe');
end;

function ExistingInstallDetected: Boolean;
begin
  Result := IsExistingInstallAtPath(WizardDirValue);
end;

function IsValidPortText(const Value: string): Boolean;
var
  Parsed: Integer;
begin
  Parsed := StrToIntDef(Trim(Value), 0);
  Result := (Parsed >= 1) and (Parsed <= 65535);
end;

{ ========================================
  AUTO-DETECTION FUNCTIONS
  ======================================== }

function DetectPostgreSQLService: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    'cmd.exe',
    '/C sc query "E_Attendance_PostgreSQL" >nul 2>&1',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function IsPostgreSQLRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    'cmd.exe',
    '/C sc query "E_Attendance_PostgreSQL" | findstr /I "RUNNING" >nul',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function CheckDatabaseExists: Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  
  if not DetectPostgreSQLService then
  begin
    Log('PostgreSQL service not detected - database check skipped.');
    Exit;
  end;
  
  if not IsPostgreSQLRunning then
  begin
    Log('PostgreSQL service not running - attempting to start...');
    Exec('cmd.exe', '/C sc start "E_Attendance_PostgreSQL" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
  
  { Simple check: try to connect to database via psql }
  if Exec('cmd.exe',
    '/C powershell -NoProfile -ExecutionPolicy Bypass -Command ' +
    '"$env:PGPASSWORD=''PB@14891277''; ' +
    '& ''C:\PostgreSQL\bin\psql.exe'' -h localhost -p 1277 -U postgres -d postgres -c ' +
    '''SELECT 1 WHERE EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name=''''E_Attendace'''');'' ' +
    '>nul 2>&1"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      Result := True;
      Log('Database E_Attendace detected.');
    end;
  end;
end;

procedure InitializeWizard;
begin
  AutoStartTaskPreset := False;
  FreshInstallRequested := False;
  ForceFreshPostgreSqlInstall := False;
  SelectedApiPort := '8081';
  SelectedWebPort := '8001';
  SkipCustomPages := False;
  PostgreSQLDetected := False;
  DatabaseExists := False;
  
  { AUTO-DETECT on startup }
  Log('Starting auto-detection...');
  PostgreSQLDetected := DetectPostgreSQLService;
  if PostgreSQLDetected then
    Log('PostgreSQL Service Detection: True')
  else
    Log('PostgreSQL Service Detection: False');
  
  if PostgreSQLDetected then
  begin
    Log('PostgreSQL detected. Checking if database E_Attendace exists...');
    DatabaseExists := CheckDatabaseExists;
    if DatabaseExists then
      Log('Database E_Attendace Check: True')
    else
      Log('Database E_Attendace Check: False');
    
    { If both PostgreSQL and database exist, skip custom pages for speed }
    if DatabaseExists then
    begin
      Log('PostgreSQL + Database both detected. Using FAST install mode.');
      SkipCustomPages := True;
    end;
  end;

  InstallModePage := CreateCustomPage(
    wpSelectDir,
    'Installation Mode',
    'Existing E-Attendance installation was found. Choose update or fresh install.');

  RbUpdateInstall := TRadioButton.Create(InstallModePage);
  RbUpdateInstall.Parent := InstallModePage.Surface;
  RbUpdateInstall.Left := ScaleX(0);
  RbUpdateInstall.Top := ScaleY(8);
  RbUpdateInstall.Width := InstallModePage.SurfaceWidth;
  RbUpdateInstall.Caption := 'Update existing installation (recommended)';
  RbUpdateInstall.Checked := True;

  RbFreshInstall := TRadioButton.Create(InstallModePage);
  RbFreshInstall.Parent := InstallModePage.Surface;
  RbFreshInstall.Left := ScaleX(0);
  RbFreshInstall.Top := RbUpdateInstall.Top + RbUpdateInstall.Height + ScaleY(10);
  RbFreshInstall.Width := InstallModePage.SurfaceWidth;
  RbFreshInstall.Caption := 'Fresh install (remove old app files, keep PostgreSQL data)';

  PortSelectionPage := CreateCustomPage(
    InstallModePage.ID,
    'Port Configuration',
    'Select API (server) and Web UI ports for this installation.');

  ApiPortLabel := TNewStaticText.Create(PortSelectionPage);
  ApiPortLabel.Parent := PortSelectionPage.Surface;
  ApiPortLabel.Left := ScaleX(0);
  ApiPortLabel.Top := ScaleY(8);
  ApiPortLabel.Caption := 'API Port (Server):';

  ApiPortEdit := TNewEdit.Create(PortSelectionPage);
  ApiPortEdit.Parent := PortSelectionPage.Surface;
  ApiPortEdit.Left := ScaleX(0);
  ApiPortEdit.Top := ApiPortLabel.Top + ScaleY(18);
  ApiPortEdit.Width := ScaleX(120);
  ApiPortEdit.Text := SelectedApiPort;

  WebPortLabel := TNewStaticText.Create(PortSelectionPage);
  WebPortLabel.Parent := PortSelectionPage.Surface;
  WebPortLabel.Left := ScaleX(0);
  WebPortLabel.Top := ApiPortEdit.Top + ApiPortEdit.Height + ScaleY(14);
  WebPortLabel.Caption := 'Web UI Port (Browser Access):';

  WebPortEdit := TNewEdit.Create(PortSelectionPage);
  WebPortEdit.Parent := PortSelectionPage.Surface;
  WebPortEdit.Left := ScaleX(0);
  WebPortEdit.Top := WebPortLabel.Top + ScaleY(18);
  WebPortEdit.Width := ScaleX(120);
  WebPortEdit.Text := SelectedWebPort;

  PostgresModePage := CreateCustomPage(
    PortSelectionPage.ID,
    'PostgreSQL Setup Mode',
    'Choose how setup should handle PostgreSQL service/data.');

  RbPgKeepInstall := TRadioButton.Create(PostgresModePage);
  RbPgKeepInstall.Parent := PostgresModePage.Surface;
  RbPgKeepInstall.Left := ScaleX(0);
  RbPgKeepInstall.Top := ScaleY(8);
  RbPgKeepInstall.Width := PostgresModePage.SurfaceWidth;
  RbPgKeepInstall.Caption := 'Keep existing E_Attendance PostgreSQL (install only if missing) - recommended';
  RbPgKeepInstall.Checked := True;

  RbPgFreshInstall := TRadioButton.Create(PostgresModePage);
  RbPgFreshInstall.Parent := PostgresModePage.Surface;
  RbPgFreshInstall.Left := ScaleX(0);
  RbPgFreshInstall.Top := RbPgKeepInstall.Top + RbPgKeepInstall.Height + ScaleY(10);
  RbPgFreshInstall.Width := PostgresModePage.SurfaceWidth;
  RbPgFreshInstall.Caption := 'Fresh PostgreSQL install (remove old E_Attendance_PostgreSQL service/data and reinstall with preset values)';

  InstallProgressPage := CreateOutputProgressPage(
    'Installing',
    'Please wait while setup completes database configuration...');
end;

procedure SetProgressSafe(const Text, SubText: string; const Pos, Max: Integer);
begin
  if InstallProgressPage <> nil then
  begin
    InstallProgressPage.SetText(Text, SubText);
    InstallProgressPage.SetProgress(Pos, Max);
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  
  { In FAST mode, skip custom pages if everything is already installed }
  if SkipCustomPages then
  begin
    if (PortSelectionPage <> nil) and (PageID = PortSelectionPage.ID) then
      Result := True;
    if (PostgresModePage <> nil) and (PageID = PostgresModePage.ID) then
      Result := True;
  end;
  
  { Always skip InstallMode if no existing install }
  if (InstallModePage <> nil) and (PageID = InstallModePage.ID) then
  begin
    Result := not ExistingInstallDetected;
    Exit;
  end;

  if Result = False then
    Exit;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (InstallModePage <> nil) and (CurPageID = InstallModePage.ID) then
  begin
    FreshInstallRequested := RbFreshInstall.Checked;
    if FreshInstallRequested then
    begin
      if MsgBox(
           'Fresh install will remove old E-Attendance application files from "' + WizardDirValue + '".' + #13#10 +
           'PostgreSQL service/data will be kept.' + #13#10#13#10 +
           'Continue?',
           mbConfirmation,
           MB_YESNO or MB_DEFBUTTON2) <> IDYES then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;

  if (PortSelectionPage <> nil) and (CurPageID = PortSelectionPage.ID) then
  begin
    if not IsValidPortText(ApiPortEdit.Text) then
    begin
      Log('Invalid API port entered. Resetting to 8081.');
      ApiPortEdit.Text := '8081';
    end;

    if not IsValidPortText(WebPortEdit.Text) then
    begin
      Log('Invalid Web UI port entered. Resetting to 8001.');
      WebPortEdit.Text := '8001';
    end;

    if Trim(ApiPortEdit.Text) = Trim(WebPortEdit.Text) then
    begin
      Log('API and Web UI ports matched. Resetting Web UI port.');
      WebPortEdit.Text := '8001';
      if Trim(ApiPortEdit.Text) = Trim(WebPortEdit.Text) then
      begin
        WebPortEdit.Text := IntToStr(StrToIntDef(Trim(ApiPortEdit.Text), 8081) + 1);
      end;
    end;

    SelectedApiPort := Trim(ApiPortEdit.Text);
    SelectedWebPort := Trim(WebPortEdit.Text);
  end;

  if (PostgresModePage <> nil) and (CurPageID = PostgresModePage.ID) then
  begin
    ForceFreshPostgreSqlInstall := RbPgFreshInstall.Checked;
    if ForceFreshPostgreSqlInstall then
    begin
      if MsgBox(
           'Fresh PostgreSQL install will remove E_Attendance_PostgreSQL service and local PostgreSQL data inside this app folder.' + #13#10 +
           'All attendance DB data in that local PostgreSQL instance will be deleted.' + #13#10#13#10 +
           'Continue?',
           mbConfirmation,
           MB_YESNO or MB_DEFBUTTON2) <> IDYES then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;

procedure StopRunningAppProcesses;
var
  ResultCode: Integer;
begin
  Exec('cmd.exe', '/C taskkill /IM "E.Attendance.exe" /F >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('cmd.exe', '/C taskkill /IM "E.Attendance.Web.exe" /F >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CleanApplicationFilesKeepPostgreSql(const InstallDir: string);
var
  FindRec: TFindRec;
  ItemPath: string;
begin
  if not DirExists(InstallDir) then
  begin
    Exit;
  end;

  if FindFirst(AddBackslash(InstallDir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name = '.') or (FindRec.Name = '..') then
        begin
          continue;
        end;

        ItemPath := AddBackslash(InstallDir) + FindRec.Name;
        if DirExists(ItemPath) then
        begin
          if CompareText(FindRec.Name, 'PostgreSQL') <> 0 then
          begin
            DelTree(ItemPath, True, True, True);
          end;
        end
        else
        begin
          DeleteFile(ItemPath);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function ServiceExists(const Name: string): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec('cmd.exe', '/C sc query "' + Name + '" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function FindAnyPostgresService: string;
var
  TmpFile: string;
  ResultCode: Integer;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  TmpFile := ExpandConstant('{tmp}\pg_services.txt');
  if Exec(
       'cmd.exe',
       '/C sc query state= all | findstr /I "SERVICE_NAME: postgresql" > "' + TmpFile + '"',
       '',
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode) and
     (ResultCode = 0) then
  begin
    if LoadStringsFromFile(TmpFile, Lines) then
    begin
      for I := 0 to GetArrayLength(Lines) - 1 do
      begin
        if Pos('SERVICE_NAME:', Lines[I]) > 0 then
        begin
          Result := Trim(Copy(Lines[I], Length('SERVICE_NAME:') + 1, MaxInt));
          Break;
        end;
      end;
    end;
  end;
end;

function RunControllerBootstrapWithTimeout(
  const ControllerExe: string;
  const WorkDir: string;
  const TimeoutSeconds: Integer;
  var ExitCode: Integer): Boolean;
var
  PsExe: string;
  PsArgs: string;
  ResultCode: Integer;
begin
  PsExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  PsArgs :=
    '-NoProfile -ExecutionPolicy Bypass -Command "' +
    '$p = Start-Process -FilePath ''' + ControllerExe + ''' -ArgumentList ''--db-bootstrap'' -WorkingDirectory ''' + WorkDir + ''' -WindowStyle Hidden -PassThru; ' +
    'if ($null -eq $p) { exit 125 }; ' +
    'if (Wait-Process -Id $p.Id -Timeout ' + IntToStr(TimeoutSeconds) + ' -ErrorAction SilentlyContinue) { exit $p.ExitCode } ' +
    'else { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 124 }"';

  Result := Exec(PsExe, PsArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then
  begin
    ExitCode := ResultCode;
  end
  else
  begin
    ExitCode := -1;
  end;
end;

procedure EnsurePostgreSqlInstalled;
var
  InstallerPath: string;
  InstallDir: string;
  DataDir: string;
  PgRootDir: string;
  Args: string;
  ResultCode: Integer;
begin
  if ForceFreshPostgreSqlInstall then
  begin
    Log('Fresh PostgreSQL reinstall requested by user.');
    SetProgressSafe('Preparing fresh PostgreSQL install...', 'Removing existing E_Attendance_PostgreSQL service and local data folder.', 56, 100);
    Exec('cmd.exe', '/C sc stop "{#PgServiceName}" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('cmd.exe', '/C taskkill /F /IM "postgres.exe" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('cmd.exe', '/C sc delete "{#PgServiceName}" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1200);

    PgRootDir := ExpandConstant('{app}\PostgreSQL');
    if DirExists(PgRootDir) then
    begin
      DelTree(PgRootDir, True, True, True);
    end;
  end;

  if ServiceExists('{#PgServiceName}') then
  begin
    Log('PostgreSQL service already available: {#PgServiceName}.');
    Exec('cmd.exe', '/C sc config "{#PgServiceName}" start= auto >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('cmd.exe', '/C sc start "{#PgServiceName}" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exit;
  end;

#if defined(PostgresInstallerPath)
  InstallerPath := ExpandConstant('{tmp}\{#PostgresInstallerName}');
  if not FileExists(InstallerPath) then
  begin
    Log('Bundled PostgreSQL installer missing at: ' + InstallerPath);
    Exit;
  end;

  InstallDir := ExpandConstant('{app}\{#PgInstallSubdir}');
  DataDir := ExpandConstant('{app}\{#PgDataSubdir}');
  ForceDirectories(InstallDir);
  ForceDirectories(DataDir);

  Args := Format('--mode unattended --unattendedmodeui minimal --prefix "%s" --datadir "%s" --superpassword "%s" --serverport %s --servicename "%s" --enable-components server,commandlinetools', [InstallDir, DataDir, '{#PgPassword}', '{#PgPort}', '{#PgServiceName}']);

  Log('Launching PostgreSQL installer in unattended mode.');
  SetProgressSafe('Installing PostgreSQL database...', 'PostgreSQL installer window will open. Please wait until it finishes.', 60, 100);

  if Exec(InstallerPath, Args, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      Log('PostgreSQL installed successfully by setup.');
      Exec('cmd.exe', '/C sc config "{#PgServiceName}" start= auto >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec('cmd.exe', '/C sc start "{#PgServiceName}" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      SetProgressSafe('PostgreSQL installed successfully.', '', 80, 100);
    end
    else
    begin
      Log('PostgreSQL installer failed with exit code: ' + IntToStr(ResultCode));
    end;
  end
  else
  begin
    Log('Failed to launch PostgreSQL installer.');
  end;
#else
  Log('No PostgresInstallerPath define was provided. Skipping PostgreSQL auto-install.');
#endif
end;

function IsPbPostgreSqlRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    'cmd.exe',
    '/C sc query "{#PgServiceName}" | findstr /I "RUNNING" >nul',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function EnsurePostgreSqlServiceReadyForBootstrap: Boolean;
var
  ServiceName: string;
  ResultCode: Integer;
  Attempt: Integer;
  MaxAttempts: Integer;
begin
  Result := False;

  ServiceName := '{#PgServiceName}';
  if not ServiceExists(ServiceName) then
  begin
    ServiceName := FindAnyPostgresService;
    if ServiceName = '' then
    begin
      Log('No PostgreSQL service found before bootstrap.');
      Exit;
    end;
  end;

  if Exec('cmd.exe', '/C sc query "' + ServiceName + '" | findstr /I "RUNNING" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
     (ResultCode = 0) then
  begin
    SetProgressSafe('PostgreSQL service check', 'E_Attendance_PostgreSQL is running.', 86, 100);
    Log('PostgreSQL service check passed: RUNNING.');
    Result := True;
    Exit;
  end;

  SetProgressSafe('Starting PostgreSQL service...', 'Trying to start E_Attendance_PostgreSQL.', 86, 100);
  Exec('cmd.exe', '/C sc start "' + ServiceName + '" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  MaxAttempts := 25;
  for Attempt := 1 to MaxAttempts do
  begin
    if Exec('cmd.exe', '/C sc query "' + ServiceName + '" | findstr /I "RUNNING" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
       (ResultCode = 0) then
    begin
      SetProgressSafe('PostgreSQL service check', 'E_Attendance_PostgreSQL started successfully.', 88, 100);
      Log('PostgreSQL service started successfully before bootstrap.');
      Result := True;
      Exit;
    end;

    Sleep(1200);
  end;

  Log('PostgreSQL service failed to reach RUNNING state before bootstrap.');
end;

function IsPostgreSqlPortReachable: Boolean;
var
  PsExe: string;
  PsArgs: string;
  ResultCode: Integer;
begin
  PsExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  PsArgs :=
    '-NoProfile -ExecutionPolicy Bypass -Command "' +
    '$c = New-Object System.Net.Sockets.TcpClient; ' +
    'try { ' +
      '$iar = $c.BeginConnect(''127.0.0.1'', {#PgPort}, $null, $null); ' +
      'if (-not $iar.AsyncWaitHandle.WaitOne(1000)) { $c.Close(); exit 1 }; ' +
      '$c.EndConnect($iar) | Out-Null; ' +
      '$c.Close(); ' +
      'exit 0 ' +
    '} catch { ' +
      'try { $c.Close() } catch {}; ' +
      'exit 1 ' +
    '}"';

  Result := Exec(PsExe, PsArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function WaitForPostgreSqlPortReady: Boolean;
var
  Attempt: Integer;
  MaxAttempts: Integer;
begin
  Result := False;
  MaxAttempts := 45;
  SetProgressSafe('Waiting for PostgreSQL to accept connections...', 'Checking TCP port {#PgPort} on localhost.', 89, 100);
  for Attempt := 1 to MaxAttempts do
  begin
    if IsPostgreSqlPortReachable then
    begin
      Result := True;
      Log('PostgreSQL TCP port is reachable: {#PgPort}.');
      Exit;
    end;

    Sleep(1000);
  end;

  Log('PostgreSQL TCP port did not become reachable within timeout: {#PgPort}.');
end;

procedure RunDatabaseBootstrap;
var
  ControllerExe: string;
  ResultCode: Integer;
  Attempt: Integer;
  MaxAttempts: Integer;
  TimeoutSeconds: Integer;
begin
  ControllerExe := ExpandConstant('{app}\E.Attendance.exe');
  if not FileExists(ControllerExe) then
  begin
    Log('E.Attendance.exe not found. Skipping DB bootstrap.');
    Exit;
  end;

  MaxAttempts := 4;
  TimeoutSeconds := 180;
  for Attempt := 1 to MaxAttempts do
  begin
    SetProgressSafe(
      'Running database bootstrap...',
      Format('Attempt %d of %d (timeout %d seconds)', [Attempt, MaxAttempts, TimeoutSeconds]),
      90,
      100);

    if RunControllerBootstrapWithTimeout(ControllerExe, ExpandConstant('{app}'), TimeoutSeconds, ResultCode) then
    begin
      if ResultCode = 0 then
      begin
        Log('Database bootstrap completed successfully.');
        Exit;
      end;

      if ResultCode = 124 then
      begin
        Log('Database bootstrap timed out and was stopped by setup' +
            ' (attempt ' + IntToStr(Attempt) + ' of ' + IntToStr(MaxAttempts) + ').');
      end
      else
      begin
        Log('Database bootstrap returned code: ' + IntToStr(ResultCode) +
          ' (attempt ' + IntToStr(Attempt) + ' of ' + IntToStr(MaxAttempts) + ').');
      end;
    end
    else
    begin
      Log('Failed to execute E.Attendance.exe --db-bootstrap' +
           ' (attempt ' + IntToStr(Attempt) + ' of ' + IntToStr(MaxAttempts) + ').');
    end;

    if Attempt < MaxAttempts then
    begin
      SetProgressSafe('Running database bootstrap...', 'Retrying in 6 seconds...', 90, 100);
      Sleep(6000);
    end;
  end;

  Log('PostgreSQL was detected/installed, but automatic DB bootstrap was incomplete.');
end;

procedure ApplySelectedPorts;
var
  ControllerExe: string;
  ResultCode: Integer;
  Args: string;
begin
  ControllerExe := ExpandConstant('{app}\E.Attendance.exe');
  if not FileExists(ControllerExe) then
  begin
    Log('E.Attendance.exe not found. Skipping port configuration.');
    Exit;
  end;

  if Trim(SelectedApiPort) = '' then
  begin
    SelectedApiPort := '8081';
  end;

  if Trim(SelectedWebPort) = '' then
  begin
    SelectedWebPort := '8001';
  end;

  Args := Format('--set-ports %s %s', [SelectedApiPort, SelectedWebPort]);
  if Exec(ControllerExe, Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      Log(Format('Port configuration applied (API=%s, WEB=%s).', [SelectedApiPort, SelectedWebPort]));
    end
    else
    begin
      Log('Port configuration command returned code: ' + IntToStr(ResultCode));
    end;
  end
  else
  begin
    Log('Failed to execute E.Attendance.exe --set-ports.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    Log('Pre-install: force-stopping running E-Attendance processes to avoid file lock errors.');
    StopRunningAppProcesses;
  end;

  if CurStep = ssPostInstall then
  begin
    try
      if InstallProgressPage <> nil then
      begin
        InstallProgressPage.Show;
      end;

      SetProgressSafe('Stopping running E-Attendance processes...', '', 10, 100);
      StopRunningAppProcesses;

      if FreshInstallRequested then
      begin
        SetProgressSafe('Cleaning previous application files...', '', 20, 100);
        CleanApplicationFilesKeepPostgreSql(WizardDirValue);
      end;

      SetProgressSafe('Applying selected ports...', '', 40, 100);
      ApplySelectedPorts;
      SetProgressSafe('Checking/Installing PostgreSQL...', '', 55, 100);
      EnsurePostgreSqlInstalled;
      if EnsurePostgreSqlServiceReadyForBootstrap then
      begin
        if WaitForPostgreSqlPortReady then
        begin
          SetProgressSafe('Running database bootstrap...', '', 90, 100);
          RunDatabaseBootstrap;
        end
        else
        begin
          Log('PostgreSQL service started, but database port {#PgPort} is not ready yet.');
        end;
      end;
      SetProgressSafe('Finalizing installation...', '', 100, 100);
    except
      Log('Auto PostgreSQL setup failed: ' + GetExceptionMessage);
    finally
      if InstallProgressPage <> nil then
      begin
        InstallProgressPage.Hide;
      end;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not AutoStartTaskPreset then
  begin
    WizardSelectTasks('autostart');
    AutoStartTaskPreset := True;
  end;
end;
