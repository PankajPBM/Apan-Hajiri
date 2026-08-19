#define MyAppId "{{AF12B76A-7F83-4F7F-8CA6-8C32A67D39C1}"
#define MyAppName "E-Attendance"
#define MyAppVersion "1.1.11"
#define MyAppPublisher "PB Tech Zone"
#define MyAppPublisherPhone "+9779814891277"

#ifndef BuildMode
  #define BuildMode "Offline"
#endif

; ── ALWAYS use the VS Release build output ────────────────────────────────
; Build in VS: Build → Build Solution (Release configuration)
; Then run: iscc.exe installer\E_Attendace.Setup.iss
; Do NOT use build-installer.ps1 — it packages old artifacts instead.
; ─────────────────────────────────────────────────────────────────────────

#ifndef AppExePath
  #if FileExists("..\E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.exe")
    #define AppExePath "..\E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.exe"
  #elif FileExists("..\E.Attendance.Web\bin\Release\net10.0\win-x64\E.Attendance.Web.exe")
    #define AppExePath "..\E.Attendance.Web\bin\Release\net10.0\win-x64\E.Attendance.Web.exe"
  #else
    #error "E.Attendance.Web.exe not found. Open VS, select Release configuration, and do Build -> Build Solution first."
  #endif
#endif
#define AppContentDir AddBackslash(ExtractFilePath(AppExePath))

#ifndef ControllerExePath
  #if FileExists("..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.exe")
    #define ControllerExePath "..\E.Attendance\bin\Release\net10.0-windows\E.Attendance.exe"
    #define ControllerBinDir "..\E.Attendance\bin\Release\net10.0-windows"
  #else
    #error "E.Attendance.exe not found. Open VS, select Release configuration, and do Build -> Build Solution first."
  #endif
#endif

#ifndef SetupIconPath
  #if FileExists("..\icon\E_Att.ico")
    #define SetupIconPath "..\icon\E_Att.ico"
  #elif FileExists("..\branding\E_Att.ico")
    #define SetupIconPath "..\branding\E_Att.ico"
  #else
    #error "SetupIconPath required. Place E_Att.ico in icon folder."
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
  #elif FileExists("D:\Pankaj\Project\E-Attendance\installer\postgresql-18.3-3.exe")
    #define PostgresInstallerPath "D:\Pankaj\Project\E-Attendance\installer\postgresql-18.3-3.exe"
  #else
    #define PostgresInstallerPath ""
  #endif
#endif

#define PgServiceName "E_Attendance_PostgreSQL"
#define PgPort "1277"
#define PgPassword "PB@14891277"
#define PgInstallSubdir "PostgreSQL\15"
#define PgDataSubdir "PostgreSQL\15\data"

; Files/folders that must NEVER be bundled into the Windows installer:
;  - *.pdb                : debug symbols
;  - PB.Attendance.*      : stale assemblies from before the project rename
;  - runtimes\<non-win>   : Linux/macOS/wasm native libs (Windows-only product). runtimes\win* is KEPT.
;  - linux-*/osx-* dirs   : stray self-contained publish outputs
;  - culture folders      : non-English .NET satellite resources
;  - win-x64\*           : stale self-contained publish leftover (NOT updated by a normal
;                          Release build). The fresh framework-dependent build lives in the
;                          net10.0 root, so this subfolder is dead weight and must be excluded
;                          to guarantee the install only contains the freshly-built binaries.
#define WasteExcludes "*.pdb,PB.Attendance.*,*\PB.Attendance.*,win-x64\*,runtimes\linux*,runtimes\osx*,runtimes\maccatalyst*,runtimes\browser*,runtimes\unix*,runtimes\android*,runtimes\ios*,runtimes\tvos*,runtimes\freebsd*,runtimes\illumos*,runtimes\solaris*,linux-arm64\*,linux-x64\*,osx-x64\*,osx-arm64\*,cs\*,de\*,es\*,fr\*,it\*,ja\*,ko\*,pl\*,pt-BR\*,ru\*,tr\*,zh-Hans\*,zh-Hant\*"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=tel:{#MyAppPublisherPhone}
AppSupportPhone={#MyAppPublisherPhone}
DefaultDirName={autopf}\E-Attendance
DefaultGroupName=E-Attendance
DisableProgramGroupPage=yes
OutputDir={#OutputDirPath}
OutputBaseFilename=E-Attendance_{#MyAppVersion}_{#BuildMode}_Setup
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

[Files]
Source: "{#AppContentDir}*"; DestDir: "{app}"; Excludes: "graphify-out\*,{#WasteExcludes}"; Flags: ignoreversion recursesubdirs createallsubdirs
#if defined(ControllerBinDir)
Source: "{#ControllerBinDir}\*"; DestDir: "{app}"; Excludes: "{#WasteExcludes}"; Flags: ignoreversion recursesubdirs createallsubdirs
#elif defined(ControllerExePath)
Source: "{#ControllerExePath}"; DestDir: "{app}"; DestName: "E.Attendance.exe"; Flags: ignoreversion
#endif

#if defined(PostgresInstallerPath) && PostgresInstallerPath != ""
Source: "{#PostgresInstallerPath}"; DestDir: "{tmp}"; DestName: "postgresql-18.3-3.exe"; Flags: deleteafterinstall ignoreversion
#endif

; Ship the HD app icon so shortcuts reference a deterministic, high-resolution .ico.
Source: "{#SetupIconPath}"; DestDir: "{app}"; DestName: "E_Att.ico"; Flags: ignoreversion

[Icons]
#if defined(ControllerExePath)
Name: "{autodesktop}\E_Att_Service_Controller"; Filename: "{app}\E.Attendance.exe"; IconFilename: "{app}\E_Att.ico"; IconIndex: 0; Tasks: desktopicon
#endif
Name: "{autodesktop}\E_Attendace"; Filename: "{code:GetWebUrl}"; IconFilename: "{app}\E_Att.ico"; IconIndex: 0; Tasks: desktopicon

[Registry]
; Extended Windows Service startup timeout from 30s to 120s so heavy DB-init does not fail
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control"; ValueType: dword; ValueName: "ServicesPipeTimeout"; ValueData: "120000"

[Run]
; The Windows service is registered by RegisterWindowsServices in [Code] (ssPostInstall),
; which uses sc.exe directly and needs neither PostgreSQL nor the .NET runtime.
Filename: "{app}\E.Attendance.exe"; Description: "Launch E-Attendance Service Controller"; Flags: nowait postinstall skipifsilent; Check: FileExists(ExpandConstant('{app}\E.Attendance.exe'))


[Code]
var
  InstallModePage: TWizardPage;
  RbUpdateInstall: TRadioButton;
  RbFreshInstall: TRadioButton;
  PortSelectionPage: TWizardPage;
  ApiPortEdit: TNewEdit;
  WebPortEdit: TNewEdit;
  SelectedApiPort: string;
  SelectedWebPort: string;
  FreshInstallRequested: Boolean;
  PostgresModePage: TWizardPage;
  RbPgKeepInstall: TRadioButton;
  RbPgFreshInstall: TRadioButton;
  ForceFreshPostgreSqlInstall: Boolean;
  InstallProgressPage: TOutputProgressWizardPage;

function GetWebUrl(Param: string): string;
begin
  Result := 'http://127.0.0.1:' + SelectedWebPort + '/';
end;

{ Must be defined first - used by all other procedures }
procedure SetProgressSafe(const Text, SubText: string; const Pos, Max: Integer);
begin
  if InstallProgressPage <> nil then
  begin
    InstallProgressPage.SetText(Text, SubText);
    InstallProgressPage.SetProgress(Pos, Max);
  end;
end;

{ ---- .NET 10 Desktop Runtime ---- }

function IsDotNet10Installed: Boolean;
begin
  { Always let the bundled installer decide - it exits in ~2s if already installed }
  Result := False;
end;

procedure InstallDotNet10Runtime;
var
  TmpFile: string;
  ResultCode: Integer;
begin
  if IsDotNet10Installed then
  begin
    Log('.NET 10 Desktop Runtime already installed. Skipping.');
    Exit;
  end;

  TmpFile := ExpandConstant('{tmp}\windowsdesktop-runtime-10.0.8-win-x64.exe');
  if not FileExists(TmpFile) then
  begin
    Log('.NET 10 bundled installer not found. Skipping.');
    Exit;
  end;

  Log('Installing .NET 10 Windows Desktop Runtime (offline)...');
  SetProgressSafe('.NET 10 Windows Desktop Runtime installing...', 'Please wait (1-2 minutes)...', 5, 100);
  Exec(TmpFile, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if ResultCode = 0 then
    Log('.NET 10 Windows Desktop Runtime installed successfully.')
  else
    Log('.NET 10 installer returned code: ' + IntToStr(ResultCode));

  SetProgressSafe('.NET 10 Windows Desktop Runtime ready.', '', 15, 100);
end;

{ ---- Helpers ---- }

function IsExistingInstallAtPath(const InstallDir: string): Boolean;
begin
  Result :=
    FileExists(AddBackslash(InstallDir) + 'E.Attendance.exe') or
    FileExists(AddBackslash(InstallDir) + 'E.Attendance.Web.exe');
end;

function ExistingInstallDetected: Boolean;
begin
  Result := IsExistingInstallAtPath(WizardDirValue);
end;

function IsValidPortText(const Value: string): Boolean;
var
  P: Integer;
begin
  P := StrToIntDef(Trim(Value), 0);
  Result := (P >= 1) and (P <= 65535);
end;

function ServiceExists(const Name: string): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec('cmd.exe', '/C sc query "' + Name + '" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

{ ---- Wizard Pages ---- }

procedure InitializeWizard;
var
  ApiPortLabel, WebPortLabel: TNewStaticText;
  PgNote: TNewStaticText;
begin
  FreshInstallRequested  := False;
  ForceFreshPostgreSqlInstall := False;
  SelectedApiPort := '8081';
  SelectedWebPort := '8001';

  InstallModePage := CreateCustomPage(wpSelectDir,
    'Installation Mode',
    'An existing E-Attendance installation was found.');

  RbUpdateInstall := TRadioButton.Create(InstallModePage);
  RbUpdateInstall.Parent  := InstallModePage.Surface;
  RbUpdateInstall.Left    := ScaleX(0);
  RbUpdateInstall.Top     := ScaleY(8);
  RbUpdateInstall.Width   := InstallModePage.SurfaceWidth;
  RbUpdateInstall.Caption := 'Update existing installation (recommended)';
  RbUpdateInstall.Checked := True;

  RbFreshInstall := TRadioButton.Create(InstallModePage);
  RbFreshInstall.Parent  := InstallModePage.Surface;
  RbFreshInstall.Left    := ScaleX(0);
  RbFreshInstall.Top     := RbUpdateInstall.Top + RbUpdateInstall.Height + ScaleY(10);
  RbFreshInstall.Width   := InstallModePage.SurfaceWidth;
  RbFreshInstall.Caption := 'Fresh install (REMOVE everything: app files + PostgreSQL data)';

  PortSelectionPage := CreateCustomPage(InstallModePage.ID,
    'Port Configuration',
    'Select API and Web UI ports.');

  ApiPortLabel := TNewStaticText.Create(PortSelectionPage);
  ApiPortLabel.Parent  := PortSelectionPage.Surface;
  ApiPortLabel.Left    := ScaleX(0);
  ApiPortLabel.Top     := ScaleY(8);
  ApiPortLabel.Caption := 'API Port (Server):';

  ApiPortEdit := TNewEdit.Create(PortSelectionPage);
  ApiPortEdit.Parent := PortSelectionPage.Surface;
  ApiPortEdit.Left   := ScaleX(0);
  ApiPortEdit.Top    := ApiPortLabel.Top + ScaleY(20);
  ApiPortEdit.Width  := ScaleX(120);
  ApiPortEdit.Text   := SelectedApiPort;

  WebPortLabel := TNewStaticText.Create(PortSelectionPage);
  WebPortLabel.Parent  := PortSelectionPage.Surface;
  WebPortLabel.Left    := ScaleX(0);
  WebPortLabel.Top     := ApiPortEdit.Top + ApiPortEdit.Height + ScaleY(16);
  WebPortLabel.Caption := 'Web UI Port (Browser Access):';

  WebPortEdit := TNewEdit.Create(PortSelectionPage);
  WebPortEdit.Parent := PortSelectionPage.Surface;
  WebPortEdit.Left   := ScaleX(0);
  WebPortEdit.Top    := WebPortLabel.Top + ScaleY(20);
  WebPortEdit.Width  := ScaleX(120);
  WebPortEdit.Text   := SelectedWebPort;

  PostgresModePage := CreateCustomPage(PortSelectionPage.ID,
    'PostgreSQL Setup Mode',
    'Choose how setup handles the PostgreSQL database.');

  RbPgKeepInstall := TRadioButton.Create(PostgresModePage);
  RbPgKeepInstall.Parent  := PostgresModePage.Surface;
  RbPgKeepInstall.Left    := ScaleX(0);
  RbPgKeepInstall.Top     := ScaleY(8);
  RbPgKeepInstall.Width   := PostgresModePage.SurfaceWidth;
  RbPgKeepInstall.Caption := 'Keep existing PostgreSQL (install only if missing) - recommended';
  RbPgKeepInstall.Checked := True;

  RbPgFreshInstall := TRadioButton.Create(PostgresModePage);
  RbPgFreshInstall.Parent  := PostgresModePage.Surface;
  RbPgFreshInstall.Left    := ScaleX(0);
  RbPgFreshInstall.Top     := RbPgKeepInstall.Top + RbPgKeepInstall.Height + ScaleY(10);
  RbPgFreshInstall.Width   := PostgresModePage.SurfaceWidth;
  RbPgFreshInstall.Caption := 'Fresh PostgreSQL install (WARNING: deletes existing attendance data)';

  PgNote := TNewStaticText.Create(PostgresModePage);
  PgNote.Parent  := PostgresModePage.Surface;
  PgNote.Left    := ScaleX(0);
  PgNote.Top     := RbPgFreshInstall.Top + RbPgFreshInstall.Height + ScaleY(16);
  PgNote.Width   := PostgresModePage.SurfaceWidth;
  PgNote.Caption :=
    'Note: PostgreSQL installs silently in the background.' + #13#10 +
    'The installer may appear inactive for several minutes - this is normal.' + #13#10 +
    'Do NOT close the installer window during this time.';

  InstallProgressPage := CreateOutputProgressPage(
    'Configuring E-Attendance',
    'Please wait while setup completes...');
end;

{ ---- Page Navigation ---- }

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (InstallModePage <> nil) and (PageID = InstallModePage.ID) then
    Result := not ExistingInstallDetected;
  if (PostgresModePage <> nil) and (PageID = PostgresModePage.ID) then
    Result := FreshInstallRequested; // Skip PostgreSQL mode when fresh install (everything removed)
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ApiVal, WebVal: Integer;
begin
  Result := True;

  if (InstallModePage <> nil) and (CurPageID = InstallModePage.ID) then
  begin
    FreshInstallRequested := RbFreshInstall.Checked;
    if FreshInstallRequested then
      if MsgBox('Fresh install REMOVES EVERYTHING: app files + PostgreSQL database + all attendance data.' + #13#10#13#10 + 'This CANNOT be undone. Continue?',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
      begin
        Result := False; Exit;
      end;
  end;

  if (PortSelectionPage <> nil) and (CurPageID = PortSelectionPage.ID) then
  begin
    if not IsValidPortText(ApiPortEdit.Text) then ApiPortEdit.Text := '8081';
    if not IsValidPortText(WebPortEdit.Text) then WebPortEdit.Text := '8001';
    ApiVal := StrToIntDef(Trim(ApiPortEdit.Text), 8081);
    WebVal := StrToIntDef(Trim(WebPortEdit.Text), 8001);
    if ApiVal = WebVal then WebPortEdit.Text := IntToStr(ApiVal + 1);
    SelectedApiPort := Trim(ApiPortEdit.Text);
    SelectedWebPort := Trim(WebPortEdit.Text);
  end;

  if (PostgresModePage <> nil) and (CurPageID = PostgresModePage.ID) then
  begin
    ForceFreshPostgreSqlInstall := RbPgFreshInstall.Checked;
    if ForceFreshPostgreSqlInstall then
      if MsgBox('This will DELETE all attendance data in the local PostgreSQL instance.' + #13#10#13#10 + 'Are you sure?',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
      begin
        Result := False; Exit;
      end;
  end;
end;

{ ---- App Process / File Helpers ---- }

procedure StopRunningAppProcesses;
var
  ResultCode: Integer;
begin
  Log('Pre-install: stopping services and processes to release file locks.');

  { 1. Stop the Windows service(s) if they exist (current + any legacy name) }
  Exec('cmd.exe', '/C sc stop "E-Attendance-API" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('cmd.exe', '/C sc stop "PB-Attendance-API" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { 2. Force-kill any lingering processes by name (current app + old PB app so their files unlock) }
  Exec('cmd.exe', '/C taskkill /F /IM "E.Attendance.exe" /T >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('cmd.exe', '/C taskkill /F /IM "E.Attendance.Web.exe" /T >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('cmd.exe', '/C taskkill /F /IM "PB.Attendance.exe" /T >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('cmd.exe', '/C taskkill /F /IM "PB.Attendance.Web.exe" /T >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { 3. Small delay to allow Windows to fully release file handles }
  Sleep(1200);
end;

procedure CleanApplicationFilesKeepPostgreSql(const InstallDir: string);
var
  FindRec: TFindRec;
  ItemPath: string;
begin
  if not DirExists(InstallDir) then Exit;
  Log('Cleaning install folder (keeping only PostgreSQL data): ' + InstallDir);
  if FindFirst(AddBackslash(InstallDir) + '*', FindRec) then
  try
    repeat
      if (FindRec.Name = '.') or (FindRec.Name = '..') then continue;
      ItemPath := AddBackslash(InstallDir) + FindRec.Name;
      if DirExists(ItemPath) then
      begin
        { Keep ONLY the PostgreSQL data folder; wipe every other folder
          (old binaries, leftover PB.Attendance files, temp, logs, etc.) }
        if CompareText(FindRec.Name, 'PostgreSQL') <> 0 then
        begin
          Log('  removing folder: ' + FindRec.Name);
          DelTree(ItemPath, True, True, True);
        end;
      end
      else
      begin
        { Delete every loose file in the root }
        Log('  removing file: ' + FindRec.Name);
        DeleteFile(ItemPath);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
  Log('Install folder cleaned. Only PostgreSQL data preserved.');
end;

procedure CleanApplicationFilesAndPostgreSql(const InstallDir: string);
var
  FindRec: TFindRec;
  ItemPath: string;
  ResultCode: Integer;
begin
  if not DirExists(InstallDir) then Exit;
  
  { Stop and remove PostgreSQL service first }
  Exec('cmd.exe', '/C sc stop "E_Attendance_PostgreSQL" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Exec('cmd.exe', '/C taskkill /F /IM "postgres.exe" /T >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Exec('cmd.exe', '/C sc delete "E_Attendance_PostgreSQL" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  
  { Remove PostgreSQL data directory }
  if DirExists(AddBackslash(InstallDir) + 'PostgreSQL') then
    DelTree(AddBackslash(InstallDir) + 'PostgreSQL', True, True, True);
  
  { Then clean ALL app files — nothing is kept }
  Log('Fresh install: wiping entire install folder: ' + InstallDir);
  if FindFirst(AddBackslash(InstallDir) + '*', FindRec) then
  try
    repeat
      if (FindRec.Name = '.') or (FindRec.Name = '..') then continue;
      ItemPath := AddBackslash(InstallDir) + FindRec.Name;
      if DirExists(ItemPath) then
      begin
        Log('  removing folder: ' + FindRec.Name);
        DelTree(ItemPath, True, True, True);
      end
      else
      begin
        Log('  removing file: ' + FindRec.Name);
        DeleteFile(ItemPath);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
  { Finally remove the now-empty folder itself so absolutely nothing remains }
  RemoveDir(InstallDir);
  Log('Fresh install: install folder fully wiped.');
end;

{ ---- PostgreSQL ---- }

procedure EnsurePostgreSqlInstalled;
var
  InstallerPath, InstallDir, DataDir, Args: string;
  ResultCode, PollCode, Tick, Pct: Integer;
  PgRootDir: string;
begin
  if ForceFreshPostgreSqlInstall then
  begin
    Log('Fresh PostgreSQL reinstall requested.');
    SetProgressSafe('Removing old PostgreSQL service...', '', 56, 100);
    Exec('cmd.exe',
      '/C sc stop "E_Attendance_PostgreSQL" >nul 2>&1 & taskkill /F /IM "postgres.exe" >nul 2>&1 & sc delete "E_Attendance_PostgreSQL" >nul 2>&1',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    PgRootDir := ExpandConstant('{app}\PostgreSQL');
    if DirExists(PgRootDir) then DelTree(PgRootDir, True, True, True);
  end;

  if ServiceExists('E_Attendance_PostgreSQL') then
  begin
    { Service is registered — but verify the binary still exists. A failed/partial
      previous install can leave a stale service entry with the files gone. }
    if FileExists(ExpandConstant('{app}\PostgreSQL\15\bin\pg_ctl.exe')) then
    begin
      Log('PostgreSQL service and binary both present. Ensuring auto-start.');
      SetProgressSafe('PostgreSQL already installed.', 'Ensuring auto-start...', 72, 100);
      Exec('cmd.exe',
        '/C sc config "E_Attendance_PostgreSQL" start= auto >nul 2>&1 & sc start "E_Attendance_PostgreSQL" >nul 2>&1',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      SetProgressSafe('PostgreSQL service ready.', '', 80, 100);
      Exit;
    end;
    { Binary missing — stale service entry. Delete it and fall through to reinstall. }
    Log('PostgreSQL service entry exists but binary missing. Removing stale entry and reinstalling.');
    SetProgressSafe('Repairing PostgreSQL installation...', 'Removing stale service...', 56, 100);
    Exec('cmd.exe',
      '/C sc stop "E_Attendance_PostgreSQL" >nul 2>&1 & taskkill /F /IM "postgres.exe" >nul 2>&1 & sc delete "E_Attendance_PostgreSQL" >nul 2>&1',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;

  InstallerPath := ExpandConstant('{tmp}\postgresql-18.3-3.exe');
  if not FileExists(InstallerPath) then
  begin
    Log('PostgreSQL installer not found. Skipping.');
    Exit;
  end;

  InstallDir := ExpandConstant('{app}\PostgreSQL\15');
  DataDir    := ExpandConstant('{app}\PostgreSQL\15\data');
  ForceDirectories(InstallDir);
  ForceDirectories(DataDir);

  Args :=
    '--mode unattended --unattendedmodeui none' +
    ' --prefix "' + InstallDir + '"' +
    ' --datadir "' + DataDir + '"' +
    ' --superpassword "PB@14891277"' +
    ' --serverport 1277' +
    ' --servicename "E_Attendance_PostgreSQL"' +
    ' --enable-components server,commandlinetools';

  Log('Launching PostgreSQL silent installer...');
  SetProgressSafe('Installing PostgreSQL...', 'Starting installer process...', 58, 100);

  { Launch non-blocking so we can animate progress while it runs }
  if not Exec(InstallerPath, Args, '', SW_HIDE, ewNoWait, ResultCode) then
  begin
    Log('Failed to launch PostgreSQL installer.');
    Exit;
  end;

  { Poll until service appears - animate progress 60->70 }
  Pct := 60;
  for Tick := 1 to 480 do
  begin
    Sleep(1000);
    Pct := 60 + (Tick * 10 div 480);
    if Pct > 70 then Pct := 70;
    SetProgressSafe(
      'Installing PostgreSQL... (' + IntToStr(Tick) + 's)',
      'Extracting files and configuring database service. Please wait...',
      Pct, 100);
    if Exec('cmd.exe', '/C sc query "E_Attendance_PostgreSQL" >nul 2>&1',
            '', SW_HIDE, ewWaitUntilTerminated, PollCode) and (PollCode = 0) then
    begin
      Log('PostgreSQL service detected after ' + IntToStr(Tick) + ' seconds.');
      ResultCode := 0;
      Break;
    end;
  end;

  if ResultCode = 0 then
  begin
    Log('PostgreSQL installed successfully.');
    SetProgressSafe('PostgreSQL installed.', 'Configuring service...', 72, 100);
    Exec('cmd.exe', '/C sc config "E_Attendance_PostgreSQL" start= auto >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('cmd.exe', '/C sc start "E_Attendance_PostgreSQL" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    SetProgressSafe('PostgreSQL installed and started.', '', 80, 100);
  end
  else
    Log('PostgreSQL install did not complete (service not found within timeout).');
end;

function EnsurePostgreSqlServiceReady: Boolean;
var
  ResultCode, Attempt: Integer;
begin
  Result := False;
  if not ServiceExists('E_Attendance_PostgreSQL') then Exit;

  if Exec('cmd.exe', '/C sc query "E_Attendance_PostgreSQL" | findstr /I "RUNNING" >nul',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    Result := True; Exit;
  end;

  SetProgressSafe('Starting PostgreSQL service...', '', 84, 100);
  Exec('cmd.exe', '/C sc start "E_Attendance_PostgreSQL" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  for Attempt := 1 to 15 do
  begin
    Sleep(1200);
    if Exec('cmd.exe', '/C sc query "E_Attendance_PostgreSQL" | findstr /I "RUNNING" >nul',
            '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    begin
      Result := True; Exit;
    end;
  end;
  Log('PostgreSQL service did not reach RUNNING state.');
end;

function WaitForPostgreSqlPort: Boolean;
var
  ResultCode, Attempt: Integer;
begin
  Result := False;
  SetProgressSafe('Waiting for PostgreSQL port...', 'Checking TCP port 1277...', 87, 100);
  for Attempt := 1 to 20 do
  begin
    { Fast check: netstat is built-in, no PowerShell startup overhead }
    if Exec('cmd.exe', '/C netstat -an | findstr ":1277 " | findstr "LISTENING" >nul 2>&1',
            '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    begin
      Result := True; Exit;
    end;
    Sleep(800);
  end;
  Log('PostgreSQL port 1277 not listening within timeout.');
end;

{ ---- Bootstrap & Ports ---- }

procedure RunDatabaseBootstrap;
var
  PsqlExe, SqlFile, Sql: string;
  ExitCode: Integer;
  AppDir: string;
begin
  AppDir   := ExpandConstant('{app}');
  PsqlExe  := AppDir + '\PostgreSQL\15\bin\psql.exe';

  if not FileExists(PsqlExe) then
  begin
    Log('psql.exe not found. Skipping DB bootstrap.');
    Exit;
  end;

  SetProgressSafe('Setting up database...', 'Checking existing database...', 90, 100);

  SqlFile := ExpandConstant('{tmp}\ea_bootstrap.sql');

  { Skip ONLY if the CORE TABLES already exist — NOT merely if the database is reachable.
    A database can exist while empty (e.g. after a reinstall), and in that case we MUST run
    the bootstrap to create the tables. ON_ERROR_STOP makes a missing auth_users return a
    non-zero exit code so we fall through and build the schema. }
  SaveStringToFile(SqlFile, 'SELECT 1 FROM "E_Attendace"."auth_users" LIMIT 1;', False);
  if Exec('cmd.exe',
    '/C set PGPASSWORD=PB@14891277 && "' + PsqlExe + '" -h 127.0.0.1 -p 1277 -U postgres -d "E_Attendace" -v ON_ERROR_STOP=1 -f "' + SqlFile + '" >nul 2>&1',
    AppDir, SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0) then
  begin
    Log('Database and core tables already exist. Skipping bootstrap.');
    SetProgressSafe('Database ready.', '', 100, 100);
    Exit;
  end;
  Log('Core tables missing or database empty — running full bootstrap.');

  { Step 1: Create DB }
  Sql := 'CREATE DATABASE "E_Attendace" WITH OWNER = postgres ENCODING = ''UTF8'' TEMPLATE = template0;';
  SaveStringToFile(SqlFile, Sql, False);
  SetProgressSafe('Creating database...', 'Step 1/3', 91, 100);
  Exec('cmd.exe',
    '/C set PGPASSWORD=PB@14891277 && "' + PsqlExe + '" -h 127.0.0.1 -p 1277 -U postgres -f "' + SqlFile + '" >nul 2>&1',
    AppDir, SW_HIDE, ewWaitUntilTerminated, ExitCode);

  { Step 2: Schema, user, extensions, search_path }
  Sql :=
    'CREATE EXTENSION IF NOT EXISTS "uuid-ossp";' + #13#10 +
    'CREATE EXTENSION IF NOT EXISTS "pg_trgm";' + #13#10 +
    'DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = ''E_Attendace'') THEN' + #13#10 +
    '  CREATE USER "E_Attendace" WITH PASSWORD ''PB@14891277''; END IF; END $$;' + #13#10 +
    'GRANT ALL PRIVILEGES ON DATABASE "E_Attendace" TO postgres;' + #13#10 +
    'GRANT CONNECT ON DATABASE "E_Attendace" TO "E_Attendace";' + #13#10 +
    'CREATE SCHEMA IF NOT EXISTS "E_Attendace" AUTHORIZATION "E_Attendace";' + #13#10 +
    'GRANT USAGE ON SCHEMA "E_Attendace" TO "E_Attendace";' + #13#10 +
    'ALTER DEFAULT PRIVILEGES IN SCHEMA "E_Attendace" GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO "E_Attendace";' + #13#10 +
    'ALTER DEFAULT PRIVILEGES IN SCHEMA "E_Attendace" GRANT ALL ON SEQUENCES TO "E_Attendace";' + #13#10 +
    'ALTER DATABASE "E_Attendace" SET timezone TO ''Asia/Kathmandu'';' + #13#10 +
    'ALTER DATABASE "E_Attendace" SET search_path TO "E_Attendace";';
  SaveStringToFile(SqlFile, Sql, False);
  SetProgressSafe('Creating database...', 'Step 2/3', 93, 100);
  Exec('cmd.exe',
    '/C set PGPASSWORD=PB@14891277 && "' + PsqlExe + '" -h 127.0.0.1 -p 1277 -U postgres -d "E_Attendace" -f "' + SqlFile + '" >nul 2>&1',
    AppDir, SW_HIDE, ewWaitUntilTerminated, ExitCode);

  { Step 3: All required tables with CORRECT schemas + patch columns for existing installs }
  Sql :=
    { Self-create the schema + role first so step 3 works even if step 2 was skipped/failed. }
    'DO $bs$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = ''E_Attendace'') THEN CREATE USER "E_Attendace" WITH PASSWORD ''PB@14891277''; END IF; END $bs$;' + #13#10 +
    'CREATE SCHEMA IF NOT EXISTS "E_Attendace" AUTHORIZATION "E_Attendace";' + #13#10 +
    'SET search_path TO "E_Attendace";' + #13#10 +

    { ── Employees (Id is TEXT to match SqliteAttendanceRepository) ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."Employees" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "EmployeeCode" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "FullName" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "FirstName" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "LastName" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Email" TEXT NULL,' + #13#10 +
    '  "Phone" TEXT NULL,' + #13#10 +
    '  "Area" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Department" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "DepartmentId" INTEGER NULL,' + #13#10 +
    '  "AreaId" INTEGER NOT NULL DEFAULT 1,' + #13#10 +
    '  "DepartmentsJson" TEXT NOT NULL DEFAULT ''[]'',' + #13#10 +
    '  "Position" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "CardNumber" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Password" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "MobileNumber" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Gender" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Privilege" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "VerifyMode" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "FaceTemplateVisible" TEXT NULL,' + #13#10 +
    '  "FaceTemplateInfrared" TEXT NULL,' + #13#10 +
    '  "ProfilePhoto" TEXT NULL,' + #13#10 +
    '  "FingerprintTemplates" TEXT NULL,' + #13#10 +
    '  "ShiftId" INTEGER NULL,' + #13#10 +
    '  "JoinDate" date NULL,' + #13#10 +
    '  "Status" TEXT NOT NULL DEFAULT ''active'',' + #13#10 +
    '  "IsActive" INTEGER NOT NULL DEFAULT 1,' + #13#10 +
    '  "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "SyncedAt" timestamptz NULL,' + #13#10 +
    '  "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    { ── AttendanceRecords (Id/EmployeeId/DeviceId TEXT to match SqliteAttendanceRepository) ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."AttendanceRecords" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "EmployeeId" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "EmployeeCode" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Timestamp" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "Status" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "StatusCode" INTEGER NULL,' + #13#10 +
    '  "VerifyMode" INTEGER NULL,' + #13#10 +
    '  "Workcode" TEXT NULL,' + #13#10 +
    '  "DeviceId" TEXT NULL,' + #13#10 +
    '  "DeviceSerialNumber" TEXT NULL,' + #13#10 +
    '  "AreaId" INTEGER NOT NULL DEFAULT 1,' + #13#10 +
    '  "DepartmentId" INTEGER NOT NULL DEFAULT 1' + #13#10 +
    ');' + #13#10 +

    { ── AppLogs ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."AppLogs" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "TimestampUtc" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "Level" TEXT NOT NULL DEFAULT ''INFO'',' + #13#10 +
    '  "Source" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Message" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Detail" TEXT NULL' + #13#10 +
    ');' + #13#10 +

    { ── TransactionDetails ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."TransactionDetails" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "ReceivedUtc" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "DeviceSerialNumber" TEXT NULL,' + #13#10 +
    '  "SourceTable" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "RawLine" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "EmployeeCode" TEXT NULL,' + #13#10 +
    '  "PunchTime" timestamptz NULL,' + #13#10 +
    '  "StatusCode" INTEGER NULL,' + #13#10 +
    '  "VerifyMode" INTEGER NULL,' + #13#10 +
    '  "Workcode" TEXT NULL,' + #13#10 +
    '  "ParsedOk" INTEGER NOT NULL DEFAULT 0' + #13#10 +
    ');' + #13#10 +

    { ── Personnel tables ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelAreas" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY DEFAULT gen_random_uuid()::text,' + #13#10 +
    '  "Code" TEXT NOT NULL UNIQUE,' + #13#10 +
    '  "Name" TEXT NOT NULL,' + #13#10 +
    '  "Remark" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelDepartments" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY DEFAULT gen_random_uuid()::text,' + #13#10 +
    '  "Code" TEXT NOT NULL UNIQUE,' + #13#10 +
    '  "Name" TEXT NOT NULL,' + #13#10 +
    '  "Superior" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Manager" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "AreaCode" TEXT NOT NULL DEFAULT ''1'',' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelPositions" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY DEFAULT gen_random_uuid()::text,' + #13#10 +
    '  "Code" TEXT NOT NULL UNIQUE,' + #13#10 +
    '  "Name" TEXT NOT NULL,' + #13#10 +
    '  "Department" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelDeviceAssignments" (' + #13#10 +
    '  "DeviceSerial" TEXT PRIMARY KEY,' + #13#10 +
    '  "AreaCode" TEXT NOT NULL DEFAULT ''1'',' + #13#10 +
    '  "DepartmentCode" TEXT NOT NULL DEFAULT ''1'',' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelEmployeeProfiles" (' + #13#10 +
    '  "EmployeeCode" TEXT PRIMARY KEY,' + #13#10 +
    '  "Privilege" TEXT NOT NULL DEFAULT ''User'',' + #13#10 +
    '  "CardNumber" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "PictureDataUrl" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "MobileNumber" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Gender" TEXT NOT NULL DEFAULT ''Male'',' + #13#10 +
    '  "HireDate" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Password" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Position" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "AreaCode" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "DepartmentCode" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "AssignmentsJson" TEXT NOT NULL DEFAULT ''[]'',' + #13#10 +
    '  "VerifyMode" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    { ── Leave management ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveTypes" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "LeaveName" TEXT NOT NULL,' + #13#10 +
    '  "IsPaid" BOOLEAN NOT NULL DEFAULT TRUE,' + #13#10 +
    '  "MaxPerYear" NUMERIC NULL,' + #13#10 +
    '  "CarryForward" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "AllowHalfDay" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "IsActive" BOOLEAN NOT NULL DEFAULT TRUE' + #13#10 +
    ');' + #13#10 +
    'CREATE UNIQUE INDEX IF NOT EXISTS "UX_LeaveTypes_LeaveName" ON "E_Attendace"."LeaveTypes" ("LeaveName");' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveRequests" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "PersonnelId" TEXT NOT NULL,' + #13#10 +
    '  "LeaveTypeId" INTEGER NOT NULL,' + #13#10 +
    '  "FromDate" date NOT NULL,' + #13#10 +
    '  "ToDate" date NOT NULL,' + #13#10 +
    '  "TotalDays" NUMERIC NOT NULL DEFAULT 0,' + #13#10 +
    '  "IsHalfDay" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "HalfDayType" TEXT NULL,' + #13#10 +
    '  "Reason" TEXT NULL,' + #13#10 +
    '  "Status" TEXT NOT NULL DEFAULT ''pending'',' + #13#10 +
    '  "AppliedDate" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "ApprovedBy" TEXT NULL,' + #13#10 +
    '  "ApprovedDate" timestamptz NULL,' + #13#10 +
    '  "CancelledDate" timestamptz NULL' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveBalances" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "PersonnelId" TEXT NOT NULL,' + #13#10 +
    '  "LeaveTypeId" INTEGER NOT NULL,' + #13#10 +
    '  "Year" INTEGER NOT NULL,' + #13#10 +
    '  "TotalAllocated" NUMERIC NOT NULL DEFAULT 0,' + #13#10 +
    '  "Used" NUMERIC NOT NULL DEFAULT 0,' + #13#10 +
    '  "Remaining" NUMERIC NOT NULL DEFAULT 0,' + #13#10 +
    '  "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveAttachments" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "LeaveRequestId" INTEGER NOT NULL,' + #13#10 +
    '  "FileName" TEXT NOT NULL,' + #13#10 +
    '  "FileUrl" TEXT NOT NULL,' + #13#10 +
    '  "UploadedAt" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "UploadedBy" TEXT NULL' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveRemarks" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "LeaveRequestId" INTEGER NOT NULL,' + #13#10 +
    '  "Action" TEXT NOT NULL,' + #13#10 +
    '  "Remarks" TEXT NULL,' + #13#10 +
    '  "ActorId" TEXT NULL,' + #13#10 +
    '  "CreatedAt" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    { ── Timetable / Shift ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."Timetables" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "TimetableName" TEXT NOT NULL,' + #13#10 +
    '  "IsActive" BOOLEAN NOT NULL DEFAULT TRUE,' + #13#10 +
    '  "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."BreakTimes" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "BreakName" TEXT NOT NULL,' + #13#10 +
    '  "StartTime" time NOT NULL,' + #13#10 +
    '  "EndTime" time NOT NULL,' + #13#10 +
    '  "DurationMinutes" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "CalculateType" TEXT NOT NULL DEFAULT ''auto_deduct'',' + #13#10 +
    '  "MinimumBreakMinutes" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "IsActive" BOOLEAN NOT NULL DEFAULT TRUE' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."TimetableDays" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "TimetableId" INTEGER NOT NULL,' + #13#10 +
    '  "DayOfWeek" INTEGER NOT NULL,' + #13#10 +
    '  "IsWeeklyOff" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "CheckInTime" time NOT NULL DEFAULT TIME ''10:00'',' + #13#10 +
    '  "CheckInStartTime" time NOT NULL DEFAULT TIME ''00:00'',' + #13#10 +
    '  "CheckInEndTime" time NOT NULL DEFAULT TIME ''10:30'',' + #13#10 +
    '  "CheckOutStartTime" time NOT NULL DEFAULT TIME ''17:00'',' + #13#10 +
    '  "CheckOutTime" time NOT NULL DEFAULT TIME ''23:59'',' + #13#10 +
    '  "CheckOutEndTime" time NOT NULL DEFAULT TIME ''00:59'',' + #13#10 +
    '  "CrossDay" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "UseBreakTime" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "BreakTimeId" INTEGER NULL' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."TimetableDayBreakTimes" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "TimetableDayId" INTEGER NOT NULL,' + #13#10 +
    '  "BreakTimeId" INTEGER NOT NULL,' + #13#10 +
    '  "SortOrder" INTEGER NOT NULL DEFAULT 0' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."EmployeeTimetables" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "PersonnelId" TEXT NOT NULL,' + #13#10 +
    '  "TimetableId" INTEGER NOT NULL,' + #13#10 +
    '  "EffectiveFrom" date NOT NULL,' + #13#10 +
    '  "IsActive" BOOLEAN NOT NULL DEFAULT TRUE' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."DepartmentTimetables" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "DepartmentId" INTEGER NOT NULL,' + #13#10 +
    '  "TimetableId" INTEGER NOT NULL,' + #13#10 +
    '  "EffectiveFrom" date NOT NULL,' + #13#10 +
    '  "IsActive" BOOLEAN NOT NULL DEFAULT TRUE' + #13#10 +
    ');' + #13#10 +

    { ── Holiday / Attendance policy ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."Holidays" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "HolidayDate" date NOT NULL,' + #13#10 +
    '  "HolidayName" TEXT NOT NULL,' + #13#10 +
    '  "Description" TEXT NULL,' + #13#10 +
    '  "IsRecurring" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "DepartmentId" INTEGER NULL,' + #13#10 +
    '  "IsActive" BOOLEAN NOT NULL DEFAULT TRUE' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."AttendancePolicies" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "WeeklyOffWorkAsOvertime" BOOLEAN NOT NULL DEFAULT TRUE,' + #13#10 +
    '  "HolidayWorkAsOvertime" BOOLEAN NOT NULL DEFAULT TRUE,' + #13#10 +
    '  "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    { ── Auth tables (CORRECT schemas matching login code) ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_users" (' + #13#10 +
    '  "Id" BIGSERIAL PRIMARY KEY,' + #13#10 +
    '  "UserName" TEXT NOT NULL,' + #13#10 +
    '  "PasswordHash" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "FullName" TEXT NULL,' + #13#10 +
    '  "Role" TEXT NOT NULL DEFAULT ''Operator'',' + #13#10 +
    '  "Status" TEXT NOT NULL DEFAULT ''active'',' + #13#10 +
    '  "FeatureAccessJson" jsonb NOT NULL DEFAULT ''{}'',' + #13#10 +
    '  "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +
    'CREATE UNIQUE INDEX IF NOT EXISTS "UX_auth_users_UserName" ON "E_Attendace"."auth_users" (LOWER(TRIM("UserName")));' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_login_attempts" (' + #13#10 +
    '  "UserName" TEXT PRIMARY KEY,' + #13#10 +
    '  "FailureCount" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "LastFailedAtUtc" timestamptz NULL,' + #13#10 +
    '  "LockedUntilUtc" timestamptz NULL,' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    { ── app_config (correct column names) ── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."app_config" (' + #13#10 +
    '  "ConfigKey" TEXT PRIMARY KEY,' + #13#10 +
    '  "JsonValue" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    { ── Schema patches for existing wrong-schema tables ── }
    { auth_users patches }
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "UserName" TEXT;' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "PasswordHash" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FullName" TEXT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "Role" TEXT NOT NULL DEFAULT ''Operator'';' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "Status" TEXT NOT NULL DEFAULT ''active'';' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FeatureAccessJson" jsonb NOT NULL DEFAULT ''{}'';' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW();' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();' + #13#10 +
    { copy Username->UserName if migrating from old schema }
    'UPDATE "E_Attendace"."auth_users" SET "UserName" = "Username" WHERE "UserName" IS NULL AND "Username" IS NOT NULL;' + #13#10 +

    { auth_login_attempts patches }
    'ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "FailureCount" INTEGER NOT NULL DEFAULT 0;' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "LastFailedAtUtc" timestamptz NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "LockedUntilUtc" timestamptz NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();' + #13#10 +

    { AttendanceRecords patches (old schema had DeviceSerial/PunchTime) }
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "EmployeeId" TEXT;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "Timestamp" timestamptz;' + #13#10 +
    'UPDATE "E_Attendace"."AttendanceRecords" SET "Timestamp" = "PunchTime" WHERE "Timestamp" IS NULL AND "PunchTime" IS NOT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "Status" INTEGER NOT NULL DEFAULT 0;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "StatusCode" INTEGER NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "VerifyMode" INTEGER NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "Workcode" TEXT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "DeviceId" TEXT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "DeviceSerialNumber" TEXT NULL;' + #13#10 +
    'UPDATE "E_Attendace"."AttendanceRecords" SET "DeviceSerialNumber" = "DeviceSerial" WHERE "DeviceSerialNumber" IS NULL AND "DeviceSerial" IS NOT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "AreaId" INTEGER NOT NULL DEFAULT 1;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "DepartmentId" INTEGER NOT NULL DEFAULT 1;' + #13#10 +
    'ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN IF NOT EXISTS "AttendanceDate" date;' + #13#10 +
    'UPDATE "E_Attendace"."AttendanceRecords" SET "AttendanceDate" = "Timestamp"::date WHERE "AttendanceDate" IS NULL;' + #13#10 +

    { app_config patches (old schema had Key/Value instead of ConfigKey/JsonValue) }
    'ALTER TABLE "E_Attendace"."app_config" ADD COLUMN IF NOT EXISTS "ConfigKey" TEXT;' + #13#10 +
    'ALTER TABLE "E_Attendace"."app_config" ADD COLUMN IF NOT EXISTS "JsonValue" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."app_config" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();' + #13#10 +
    'UPDATE "E_Attendace"."app_config" SET "ConfigKey" = "Key", "JsonValue" = "Value" WHERE "ConfigKey" IS NULL AND "Key" IS NOT NULL;' + #13#10 +

    { Default data }
    'INSERT INTO "E_Attendace"."AttendancePolicies" ("WeeklyOffWorkAsOvertime","HolidayWorkAsOvertime") SELECT true,true WHERE NOT EXISTS (SELECT 1 FROM "E_Attendace"."AttendancePolicies");' + #13#10 +
    'INSERT INTO "E_Attendace"."PersonnelAreas" ("Code","Name","Remark") VALUES (''1'',''Default Area'','''') ON CONFLICT ("Code") DO NOTHING;' + #13#10 +
    'INSERT INTO "E_Attendace"."PersonnelDepartments" ("Code","Name","AreaCode") VALUES (''1'',''Default Department'',''1'') ON CONFLICT ("Code") DO NOTHING;' + #13#10 +
    'INSERT INTO "E_Attendace"."LeaveTypes" ("LeaveName","IsPaid","MaxPerYear") VALUES (''Casual Leave'',true,12) ON CONFLICT DO NOTHING;' + #13#10 +
    'INSERT INTO "E_Attendace"."LeaveTypes" ("LeaveName","IsPaid","MaxPerYear") VALUES (''Sick Leave'',true,10) ON CONFLICT DO NOTHING;' + #13#10 +
    'INSERT INTO "E_Attendace"."LeaveTypes" ("LeaveName","IsPaid","MaxPerYear") VALUES (''Annual Leave'',true,15) ON CONFLICT DO NOTHING;' + #13#10 +
    { Default admin — password is PB@TechZone (md5 hash). Will be migrated to correct format on first startup. }
    'INSERT INTO "E_Attendace"."auth_users" ("UserName","PasswordHash","FullName","Role","Status","FeatureAccessJson") VALUES (''admin'',md5(''PB@TechZone''),''Admin User'',''Admin'',''active'',''{}'') ON CONFLICT DO NOTHING;' + #13#10 +

    { auth_login_attempts — correct schema for login lockout }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_login_attempts" (' + #13#10 +
    '  "UserName" TEXT PRIMARY KEY,' + #13#10 +
    '  "FailureCount" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "LastFailedAtUtc" timestamptz NULL,' + #13#10 +
    '  "LockedUntilUtc" timestamptz NULL,' + #13#10 +
    '  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()' + #13#10 +
    ');' + #13#10 +

    { ─── Devices (Id TEXT, all booleans INTEGER, to match SqliteAttendanceRepository) ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."Devices" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "SerialNumber" TEXT NOT NULL,' + #13#10 +
    '  "Name" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Model" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "IpAddress" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Location" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "TimeZone" TEXT NOT NULL DEFAULT ''+5:45'',' + #13#10 +
    '  "SupportsVisibleLightFace" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "SupportsInfraredFace" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "SupportsFingerprint" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "FaceAlgorithmVersion" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "FingerprintAlgorithmVersion" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "FirmwareVersion" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "DeviceType" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "PushVer" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "Language" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "MultiBioDataSupport" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "MultiBioPhotoSupport" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "MultiBioVersion" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "MaxMultiBioDataCount" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "MaxMultiBioPhotoCount" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "FingerFunOn" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "FaceFunOn" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "VisiLightFunOn" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "BioDataFun" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "BioPhotoFun" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "SupportsPalm" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "SupportsFingerVein" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "CapabilitiesJson" JSONB NOT NULL DEFAULT ''{}''::jsonb,' + #13#10 +
    '  "CreatedAtUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "UpdatedAtUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "Status" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "IsRegistrationDevice" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "IsOnline" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "IsRegistered" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "LastSeenUtc" TIMESTAMPTZ NULL,' + #13#10 +
    '  "AreaId" INTEGER NOT NULL DEFAULT 1,' + #13#10 +
    '  "DepartmentId" INTEGER NOT NULL DEFAULT 1' + #13#10 +
    ');' + #13#10 +
    'CREATE UNIQUE INDEX IF NOT EXISTS "IX_Devices_SerialNumber" ON "E_Attendace"."Devices" ("SerialNumber");' + #13#10 +

    { ─── DeviceCommands (Id TEXT) ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."DeviceCommands" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "DeviceSerialNumber" TEXT NOT NULL,' + #13#10 +
    '  "CommandText" TEXT NOT NULL,' + #13#10 +
    '  "CreatedUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "SentUtc" TIMESTAMPTZ NULL,' + #13#10 +
    '  "ReplyText" TEXT NULL' + #13#10 +
    ');' + #13#10 +
    'CREATE INDEX IF NOT EXISTS "IX_DeviceCommands_Pending" ON "E_Attendace"."DeviceCommands" ("DeviceSerialNumber","SentUtc");' + #13#10 +

    { ─── DeviceSyncLogs (Id TEXT) ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."DeviceSyncLogs" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "DeviceSerialNumber" TEXT NOT NULL,' + #13#10 +
    '  "Type" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "Status" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "CommandText" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "ResponseText" TEXT NULL,' + #13#10 +
    '  "CreatedUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "CompletedUtc" TIMESTAMPTZ NULL' + #13#10 +
    ');' + #13#10 +
    'CREATE INDEX IF NOT EXISTS "IX_DeviceSyncLogs_Status" ON "E_Attendace"."DeviceSyncLogs" ("DeviceSerialNumber","Status","CreatedUtc");' + #13#10 +

    { ─── SyncLogs (the table SqliteAttendanceRepository.AddSyncLog/GetSyncLogs actually use) ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."SyncLogs" (' + #13#10 +
    '  "Id" TEXT PRIMARY KEY,' + #13#10 +
    '  "DeviceSerialNumber" TEXT NOT NULL,' + #13#10 +
    '  "Type" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "Status" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "CommandText" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "ResponseText" TEXT NULL,' + #13#10 +
    '  "CreatedUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "CompletedUtc" TIMESTAMPTZ NULL' + #13#10 +
    ');' + #13#10 +
    'CREATE INDEX IF NOT EXISTS "IX_SyncLogs_DeviceStatus" ON "E_Attendace"."SyncLogs" ("DeviceSerialNumber","Status","CreatedUtc");' + #13#10 +

    { ─── DeviceTableStamps ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."DeviceTableStamps" (' + #13#10 +
    '  "DeviceSerialNumber" TEXT NOT NULL,' + #13#10 +
    '  "TableName" TEXT NOT NULL,' + #13#10 +
    '  "StampValue" BIGINT NOT NULL DEFAULT 0,' + #13#10 +
    '  "UpdatedUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  PRIMARY KEY ("DeviceSerialNumber","TableName")' + #13#10 +
    ');' + #13#10 +

    { ─── Areas (integer-based hierarchy) ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."Areas" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "Name" TEXT NOT NULL,' + #13#10 +
    '  "IsDefault" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "UpdatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  UNIQUE ("Name")' + #13#10 +
    ');' + #13#10 +

    { ─── Departments ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."Departments" (' + #13#10 +
    '  "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,' + #13#10 +
    '  "Name" TEXT NOT NULL,' + #13#10 +
    '  "AreaId" INTEGER NOT NULL DEFAULT 1,' + #13#10 +
    '  "IsDefault" BOOLEAN NOT NULL DEFAULT FALSE,' + #13#10 +
    '  "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "UpdatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  UNIQUE ("AreaId","Name")' + #13#10 +
    ');' + #13#10 +

    { ─── EmployeeDepartments ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."EmployeeDepartments" (' + #13#10 +
    '  "EmployeeId" TEXT NOT NULL,' + #13#10 +
    '  "DepartmentId" INTEGER NOT NULL,' + #13#10 +
    '  "AssignedAt" TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  "AssignedBy" TEXT NULL,' + #13#10 +
    '  PRIMARY KEY ("EmployeeId","DepartmentId")' + #13#10 +
    ');' + #13#10 +
    'CREATE INDEX IF NOT EXISTS "IX_EmpDept_EmpId" ON "E_Attendace"."EmployeeDepartments" ("EmployeeId");' + #13#10 +
    'CREATE INDEX IF NOT EXISTS "IX_EmpDept_DeptId" ON "E_Attendace"."EmployeeDepartments" ("DepartmentId");' + #13#10 +

    { ─── SyncQueue ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."SyncQueue" (' + #13#10 +
    '  "Id" BIGSERIAL PRIMARY KEY,' + #13#10 +
    '  "EntityType" TEXT NOT NULL,' + #13#10 +
    '  "EntityId" TEXT NOT NULL,' + #13#10 +
    '  "Operation" TEXT NOT NULL,' + #13#10 +
    '  "Status" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "RetryCount" INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  "MaxRetries" INTEGER NOT NULL DEFAULT 3,' + #13#10 +
    '  "Payload" TEXT NULL,' + #13#10 +
    '  "ErrorMessage" TEXT NULL,' + #13#10 +
    '  "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),' + #13#10 +
    '  "ProcessedAt" TIMESTAMPTZ NULL,' + #13#10 +
    '  "NextRetryAt" TIMESTAMPTZ NULL' + #13#10 +
    ');' + #13#10 +
    'CREATE INDEX IF NOT EXISTS "IX_SyncQueue_Status" ON "E_Attendace"."SyncQueue" ("Status");' + #13#10 +

    { ─── Biometric tables ─── }
    'CREATE TABLE IF NOT EXISTS "E_Attendace"."users" (' + #13#10 +
    '  pin VARCHAR(50) PRIMARY KEY,' + #13#10 +
    '  name VARCHAR(255),' + #13#10 +
    '  privilege INT,' + #13#10 +
    '  password VARCHAR(255),' + #13#10 +
    '  primary_card VARCHAR(255),' + #13#10 +
    '  secondary_card VARCHAR(255),' + #13#10 +
    '  group_id INT,' + #13#10 +
    '  timezone_profile VARCHAR(32),' + #13#10 +
    '  verify_mode INT,' + #13#10 +
    '  valid_from TEXT,' + #13#10 +
    '  valid_to TEXT,' + #13#10 +
    '  expires_enabled INTEGER,' + #13#10 +
    '  phone VARCHAR(50),' + #13#10 +
    '  gender INT,' + #13#10 +
    '  nation INT,' + #13#10 +
    '  id_number VARCHAR(50),' + #13#10 +
    '  created_at TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  updated_at TEXT NOT NULL DEFAULT ''''' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."biometric_templates" (' + #13#10 +
    '  id BIGSERIAL PRIMARY KEY,' + #13#10 +
    '  user_pin VARCHAR(50) NOT NULL REFERENCES "E_Attendace"."users"(pin) ON DELETE CASCADE,' + #13#10 +
    '  type INT NOT NULL,' + #13#10 +
    '  no INT NOT NULL,' + #13#10 +
    '  idx INT NOT NULL,' + #13#10 +
    '  valid INTEGER NOT NULL DEFAULT 1,' + #13#10 +
    '  duress INTEGER NOT NULL DEFAULT 0,' + #13#10 +
    '  major_ver INT,' + #13#10 +
    '  minor_ver INT,' + #13#10 +
    '  format INT,' + #13#10 +
    '  template_data TEXT,' + #13#10 +
    '  created_at TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  updated_at TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  UNIQUE (user_pin, type, no, idx)' + #13#10 +
    ');' + #13#10 +

    'CREATE TABLE IF NOT EXISTS "E_Attendace"."biometric_photos" (' + #13#10 +
    '  id BIGSERIAL PRIMARY KEY,' + #13#10 +
    '  user_pin VARCHAR(50) NOT NULL REFERENCES "E_Attendace"."users"(pin) ON DELETE CASCADE,' + #13#10 +
    '  type INT NOT NULL,' + #13#10 +
    '  no INT NOT NULL,' + #13#10 +
    '  idx INT NOT NULL,' + #13#10 +
    '  format INT,' + #13#10 +
    '  content TEXT,' + #13#10 +
    '  created_at TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  updated_at TEXT NOT NULL DEFAULT '''',' + #13#10 +
    '  UNIQUE (user_pin, type, no, idx)' + #13#10 +
    ');' + #13#10 +

    { ─── Extra columns on Employees (biometric/sync fields) ─── }
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Area" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Department" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "AreaId" INTEGER NOT NULL DEFAULT 1;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "DepartmentId" INTEGER NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "DepartmentsJson" TEXT NOT NULL DEFAULT ''[]'';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Position" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "CardNumber" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Password" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "MobileNumber" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Gender" TEXT NOT NULL DEFAULT '''';' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Privilege" INTEGER NOT NULL DEFAULT 0;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "VerifyMode" INTEGER NOT NULL DEFAULT 0;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FaceTemplateVisible" TEXT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FaceTemplateInfrared" TEXT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "ProfilePhoto" TEXT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FingerprintTemplates" TEXT NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "SyncedAt" TIMESTAMPTZ NULL;' + #13#10 +
    'ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "IsActive" INTEGER NOT NULL DEFAULT 1;' + #13#10 +

    { ─── Seed defaults ─── }
    'INSERT INTO "E_Attendace"."Areas" ("Name","IsDefault") VALUES (''Default Area'',TRUE) ON CONFLICT ("Name") DO NOTHING;' + #13#10 +
    'INSERT INTO "E_Attendace"."Departments" ("Name","AreaId","IsDefault") VALUES (''Default Department'',1,TRUE) ON CONFLICT ("AreaId","Name") DO NOTHING;' + #13#10 +

    { ─── Type reconciliation: heal a preserved/old database whose columns were created
          with the wrong types (uuid Ids / boolean flags) by an earlier version. The C#
          repository inserts TEXT ids and INTEGER booleans, so these MUST match. Runs as
          postgres (full privileges) on every install. Idempotent — only fires when a
          column is currently the wrong type, and leaves Leave*/Timetable uuid columns alone. ─── }
    'DO $r$' + #13#10 +
    'DECLARE t TEXT; c TEXT; i INT;' + #13#10 +
    '  idt TEXT[][] := ARRAY[[''Devices'',''Id''],[''Employees'',''Id''],[''DeviceCommands'',''Id''],[''DeviceSyncLogs'',''Id''],[''AttendanceRecords'',''Id''],[''AttendanceRecords'',''EmployeeId''],[''AttendanceRecords'',''DeviceId'']];' + #13#10 +
    '  bt TEXT[][] := ARRAY[[''Devices'',''SupportsVisibleLightFace''],[''Devices'',''SupportsInfraredFace''],[''Devices'',''SupportsFingerprint''],[''Devices'',''FingerFunOn''],[''Devices'',''FaceFunOn''],[''Devices'',''VisiLightFunOn''],[''Devices'',''BioDataFun''],[''Devices'',''BioPhotoFun''],[''Devices'',''SupportsPalm''],[''Devices'',''SupportsFingerVein''],[''Devices'',''IsRegistrationDevice''],[''Devices'',''IsOnline''],[''Devices'',''IsRegistered''],[''Employees'',''IsActive''],[''users'',''expires_enabled''],[''biometric_templates'',''valid''],[''biometric_templates'',''duress'']];' + #13#10 +
    '  tst TEXT[][] := ARRAY[[''users'',''created_at''],[''users'',''updated_at''],[''users'',''valid_from''],[''users'',''valid_to''],[''biometric_templates'',''created_at''],[''biometric_templates'',''updated_at''],[''biometric_photos'',''created_at''],[''biometric_photos'',''updated_at'']];' + #13#10 +
    'BEGIN' + #13#10 +
    '  FOR i IN 1..array_length(idt,1) LOOP' + #13#10 +
    '    t:=idt[i][1]; c:=idt[i][2];' + #13#10 +
    '    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=''E_Attendace'' AND table_name=t AND column_name=c AND data_type=''uuid'') THEN' + #13#10 +
    '      IF c=''Id'' THEN EXECUTE format(''ALTER TABLE "E_Attendace".%I DROP CONSTRAINT IF EXISTS %I CASCADE'', t, t||''_pkey''); END IF;' + #13#10 +
    '      EXECUTE format(''ALTER TABLE "E_Attendace".%I ALTER COLUMN %I DROP DEFAULT'', t, c);' + #13#10 +
    '      EXECUTE format(''ALTER TABLE "E_Attendace".%I ALTER COLUMN %I TYPE TEXT USING %I::text'', t, c, c);' + #13#10 +
    '      IF c=''Id'' THEN EXECUTE format(''ALTER TABLE "E_Attendace".%I ADD PRIMARY KEY (%I)'', t, c); END IF;' + #13#10 +
    '    END IF;' + #13#10 +
    '  END LOOP;' + #13#10 +
    '  FOR i IN 1..array_length(bt,1) LOOP' + #13#10 +
    '    t:=bt[i][1]; c:=bt[i][2];' + #13#10 +
    '    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=''E_Attendace'' AND table_name=t AND column_name=c AND data_type=''boolean'') THEN' + #13#10 +
    '      EXECUTE format(''ALTER TABLE "E_Attendace".%I ALTER COLUMN %I DROP DEFAULT'', t, c);' + #13#10 +
    '      EXECUTE format(''ALTER TABLE "E_Attendace".%I ALTER COLUMN %I TYPE INTEGER USING (CASE WHEN %I THEN 1 ELSE 0 END)'', t, c, c);' + #13#10 +
    '      EXECUTE format(''ALTER TABLE "E_Attendace".%I ALTER COLUMN %I SET DEFAULT 0'', t, c);' + #13#10 +
    '    END IF;' + #13#10 +
    '  END LOOP;' + #13#10 +
    '  FOR i IN 1..array_length(tst,1) LOOP' + #13#10 +
    '    t:=tst[i][1]; c:=tst[i][2];' + #13#10 +
    '    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=''E_Attendace'' AND table_name=t AND column_name=c AND data_type LIKE ''timestamp%'') THEN' + #13#10 +
    '      EXECUTE format(''ALTER TABLE "E_Attendace".%I ALTER COLUMN %I DROP DEFAULT'', t, c);' + #13#10 +
    '      EXECUTE format(''ALTER TABLE "E_Attendace".%I ALTER COLUMN %I TYPE TEXT USING %I::text'', t, c, c);' + #13#10 +
    '    END IF;' + #13#10 +
    '  END LOOP;' + #13#10 +
    '  -- EmployeeDepartments has a composite PK; convert its uuid columns to text explicitly.' + #13#10 +
    '  IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=''E_Attendace'' AND table_name=''EmployeeDepartments'' AND column_name=''EmployeeId'' AND data_type=''uuid'') THEN' + #13#10 +
    '    ALTER TABLE "E_Attendace"."EmployeeDepartments" DROP CONSTRAINT IF EXISTS "EmployeeDepartments_pkey" CASCADE;' + #13#10 +
    '    ALTER TABLE "E_Attendace"."EmployeeDepartments" ALTER COLUMN "EmployeeId" TYPE TEXT USING "EmployeeId"::text;' + #13#10 +
    '    ALTER TABLE "E_Attendace"."EmployeeDepartments" ADD PRIMARY KEY ("EmployeeId","DepartmentId");' + #13#10 +
    '  END IF;' + #13#10 +
    '  IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=''E_Attendace'' AND table_name=''EmployeeDepartments'' AND column_name=''AssignedBy'' AND data_type=''uuid'') THEN' + #13#10 +
    '    ALTER TABLE "E_Attendace"."EmployeeDepartments" ALTER COLUMN "AssignedBy" TYPE TEXT USING "AssignedBy"::text;' + #13#10 +
    '  END IF;' + #13#10 +
    'END $r$;' + #13#10 +

    { Grant on all newly created tables }
    'GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA "E_Attendace" TO "E_Attendace";' + #13#10 +
    'GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA "E_Attendace" TO "E_Attendace";';

  SaveStringToFile(SqlFile, Sql, False);
  SetProgressSafe('Creating database...', 'Step 3/3: Tables and preset data', 96, 100);
  Exec('cmd.exe',
    '/C set PGPASSWORD=PB@14891277 && "' + PsqlExe + '" -h 127.0.0.1 -p 1277 -U postgres -d "E_Attendace" -f "' + SqlFile + '" >nul 2>&1',
    AppDir, SW_HIDE, ewWaitUntilTerminated, ExitCode);

  DeleteFile(SqlFile);
  Log('Database bootstrap complete (psql direct).');
  SetProgressSafe('Database ready.', '', 100, 100);
end;

procedure ApplySelectedPorts;
var
  SettingsDir, SettingsFile, Json: string;
begin
  if Trim(SelectedApiPort) = '' then SelectedApiPort := '8081';
  if Trim(SelectedWebPort) = '' then SelectedWebPort := '8001';

  SetProgressSafe('Saving port configuration...', 'API=' + SelectedApiPort + '  Web=' + SelectedWebPort, 40, 100);

  { Write websettings.json directly - same format as WebServiceManager.SavePorts }
  SettingsDir  := ExpandConstant('{commonappdata}\E_Attendance');
  SettingsFile := SettingsDir + '\websettings.json';
  ForceDirectories(SettingsDir);
  Json := '{' + #13#10 +
          '  "Port": ' + SelectedApiPort + ',' + #13#10 +
          '  "FrontendPort": ' + SelectedWebPort + #13#10 +
          '}';
  SaveStringToFile(SettingsFile, Json, False);

  Log('Port config written: ' + Json);
  SetProgressSafe('Port configuration saved.', '', 45, 100);
end;

{ ---- Main Install Steps ---- }

procedure RegisterWindowsServices; forward;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ServiceResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Log('Pre-install: stopping running processes and cleaning old files.');
    StopRunningAppProcesses;
    { Clean BEFORE Inno Setup copies new files (ssInstall = file-copy phase) }
    if FreshInstallRequested then
    begin
      Log('Fresh install: removing ALL files including PostgreSQL.');
      CleanApplicationFilesAndPostgreSql(WizardDirValue);
    end
    else
    begin
      Log('Update: cleaning old app files, keeping PostgreSQL folder.');
      CleanApplicationFilesKeepPostgreSql(WizardDirValue);
    end;

    { Create shared license directory so the service can write trial-state.json there }
    ForceDirectories(ExpandConstant('{commonappdata}\E_Attendance\license'));

    { Delete old trial-state.json from ALL known locations so each install gets a fresh 30-day trial.
      The service runs as LocalSystem whose %APPDATA% is inside C:\Windows\System32\config\...
      We now store trial state in C:\ProgramData\E_Attendance\license (shared, same for all accounts). }
    { Primary location (new path, used by current and future builds) }
    // DeleteFile(ExpandConstant('{commonappdata}\E_Attendance\license\trial-state.json'));
    { Legacy locations (old builds stored here — clean up so they don't cause confusion) }
    // DeleteFile(ExpandConstant('{userappdata}\PB_Attendance\trial-state.json'));
    // DeleteFile(ExpandConstant('{userappdata}\PB_Attendance\license\trial-state.json'));
    { LocalSystem account legacy path }
    // DeleteFile(ExpandConstant('{sys}\config\systemprofile\AppData\Roaming\PB_Attendance\trial-state.json'));
    Log('Preserved trial-state.json across installations.');
  end;

  if CurStep = ssPostInstall then
  begin
    try
      if InstallProgressPage <> nil then InstallProgressPage.Show;

      { ---- PostgreSQL + DB bootstrap (isolated: a failure here must NOT skip the
             Windows-service registration that follows) ---- }
      try
        SetProgressSafe('Saving port configuration...', '', 38, 100);
        ApplySelectedPorts;

        SetProgressSafe('Checking PostgreSQL installation...', '', 55, 100);
        EnsurePostgreSqlInstalled;

        if EnsurePostgreSqlServiceReady then
        begin
          if WaitForPostgreSqlPort then
          begin
            SetProgressSafe('Running database bootstrap...', 'Initializing database schema...', 90, 100);
            RunDatabaseBootstrap;
          end
          else
            Log('PostgreSQL port not ready. Bootstrap skipped.');
        end
        else
          Log('PostgreSQL service not ready. Bootstrap skipped.');
      except
        Log('PostgreSQL/bootstrap error (continuing to service install): ' + GetExceptionMessage);
      end;

      { ---- Register Windows services — own try/except so it ALWAYS runs even if the
             PostgreSQL/bootstrap step above failed. sc create needs neither PostgreSQL
             nor the .NET runtime, so the service is registered by default on every install. ---- }
      try
        SetProgressSafe('Registering Windows services...', 'E-Attendance-API', 96, 100);
        RegisterWindowsServices;
      except
        Log('Service registration error: ' + GetExceptionMessage);
      end;

      { Refresh the Windows icon cache so the new HD shortcut icon shows immediately
        instead of a stale cached one from a previous install. }
      try
        Exec('ie4uinit.exe', '-show', '', SW_HIDE, ewNoWait, ServiceResultCode);
      except
        Log('Icon cache refresh skipped: ' + GetExceptionMessage);
      end;

      SetProgressSafe('Installation complete!', '', 100, 100);
      Sleep(600);
    except
      Log('Post-install error: ' + GetExceptionMessage);
    finally
      if InstallProgressPage <> nil then InstallProgressPage.Hide;
    end;
  end;
end;

procedure RegisterWindowsServices;
var
  WebExe, ScBinPath: string;
  ResultCode, Attempt: Integer;
begin
  WebExe := ExpandConstant('{app}') + '\E.Attendance.Web.exe';
  if not FileExists(WebExe) then
  begin
    Log('E.Attendance.Web.exe not found. Skipping service registration.');
    Exit;
  end;

  { sc.exe binPath: the exe path MUST be quoted because "C:\Program Files\E-Attendance" has a
    space. The whole binPath value is wrapped in quotes below (binPath= "..."), so the inner
    quotes around the exe path must be ESCAPED as \" — sc.exe's argument parser turns \" into a
    literal quote. Using plain " here produces binPath= ""exe" args" which sc mis-parses and the
    service is never created. }
  ScBinPath := '\"' + WebExe + '\" --urls http://0.0.0.0:' + SelectedApiPort + ';http://0.0.0.0:' + SelectedWebPort + ' --service-mode';

  { ---- Step 1: Stop & delete any old service instances ---- }
  SetProgressSafe('Removing old service instances...', '', 93, 100);
  Exec('cmd.exe', '/C sc stop "E-Attendance-API" >nul 2>&1',  '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Exec('cmd.exe', '/C taskkill /F /IM "E.Attendance.Web.exe" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(600);
  Exec('cmd.exe', '/C sc delete "E-Attendance-API" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);

  { ---- Step 2: Set SCM startup timeout to 2 minutes (prevents Error 1053) ---- }
  Exec('cmd.exe',
    '/C reg add "HKLM\SYSTEM\CurrentControlSet\Control" /v ServicesPipeTimeout /t REG_DWORD /d 120000 /f >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { ---- Step 3: Create E-Attendance-API as DELAYED-AUTO-START with PostgreSQL dependency ---- }
  SetProgressSafe('Installing E-Attendance-API service...', '', 94, 100);
  Exec('cmd.exe',
    '/C sc create "E-Attendance-API"' +
    ' binPath= "' + ScBinPath + '"' +
    ' DisplayName= "E-Attendance Service"' +
    ' start= delayed-auto' +
    ' depend= "E_Attendance_PostgreSQL"' +
    ' obj= LocalSystem >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { If dependency failed (e.g., custom PG service name), retry without it }
  if not Exec('cmd.exe', '/C sc query "E-Attendance-API" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     or (ResultCode <> 0) then
  begin
    Exec('cmd.exe',
      '/C sc create "E-Attendance-API"' +
      ' binPath= "' + ScBinPath + '"' +
      ' DisplayName= "E-Attendance Service"' +
      ' start= delayed-auto' +
      ' obj= LocalSystem >nul 2>&1',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  { Description }
  Exec('cmd.exe',
    '/C sc description "E-Attendance-API" "E-Attendance API and Web UI - starts automatically after Windows boot"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Auto-restart on failure: 5 s, 15 s, 30 s; reset after 1 day }
  Exec('cmd.exe',
    '/C sc failure "E-Attendance-API" reset= 86400 actions= restart/5000/restart/15000/restart/30000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Enable restart even on non-zero exit code }
  Exec('cmd.exe',
    '/C sc failureflag "E-Attendance-API" 1 >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Ensure PostgreSQL service itself is set to auto-start }
  Exec('cmd.exe',
    '/C sc config "E_Attendance_PostgreSQL" start= auto >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { ---- Step 4: Start E-Attendance-API now and wait up to 90 s for RUNNING ---- }
  SetProgressSafe('Starting E-Attendance-API service...', 'Please wait...', 97, 100);
  Exec('cmd.exe', '/C sc start "E-Attendance-API" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  ResultCode := 1;
  for Attempt := 1 to 60 do
  begin
    Sleep(1500);
    if Exec('cmd.exe', '/C sc query "E-Attendance-API" | findstr /I "RUNNING" >nul',
            '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    begin
      Log('E-Attendance-API reached RUNNING after ' + IntToStr(Attempt * 2) + 's.');
      Break;
    end;
  end;

  SetProgressSafe('Services installed and running.', 'E-Attendance starts automatically every time Windows boots.', 100, 100);
  Log('Windows services registered via sc.exe and started successfully.');
end;


procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Stop the service, kill processes, then delete the service. Registry-key delete is a
      last-resort fallback in case sc delete is blocked. }
    Exec('cmd.exe', '/C sc stop "E-Attendance-API" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
    Exec('cmd.exe',
      '/C taskkill /F /IM "E.Attendance.Web.exe" >nul 2>&1 & taskkill /F /IM "E.Attendance.exe" >nul 2>&1',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
    Exec('cmd.exe', '/C sc delete "E-Attendance-API" >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteKeyIncludingSubkeys(HKLM, 'SYSTEM\CurrentControlSet\Services\E-Attendance-API');
  end;
end;
