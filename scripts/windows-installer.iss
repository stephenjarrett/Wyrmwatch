#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef BuildDirectory
  #error BuildDirectory is required
#endif
#ifndef PackageDirectory
  #error PackageDirectory is required
#endif
#ifndef RetiredFilesInclude
  #error RetiredFilesInclude is required
#endif

[Setup]
AppId={{6B862A4A-E39B-4A55-811F-2611E1B585F2}
AppName=Wyrmwatch
AppVersion={#AppVersion}
AppPublisher=Stephen Jarrett and Wyrmwatch contributors
AppPublisherURL=https://github.com/stephenjarrett/Wyrmwatch
AppSupportURL=https://github.com/stephenjarrett/Wyrmwatch/issues
DefaultDirName={localappdata}\Programs\Wyrmwatch
DefaultGroupName=Wyrmwatch
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
LicenseFile={#BuildDirectory}\LICENSE
OutputDir={#PackageDirectory}
OutputBaseFilename=Wyrmwatch-{#AppVersion}-win-x64-setup
UninstallDisplayIcon={app}\Wyrmwatch.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes

#include RetiredFilesInclude

[Files]
; The same source blocks are reused by Inno. Recovery runs outside the installed
; tree and outside Inno's automatically removed temporary directory.
Source: "{#BuildDirectory}\*"; DestDir: "{tmp}\support"; Flags: dontcopy recursesubdirs createallsubdirs
Source: "{#BuildDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\Wyrmwatch"; Filename: "{app}\Wyrmwatch.exe"; WorkingDir: "{app}"; Check: CreateStartMenuShortcut
Name: "{autodesktop}\Wyrmwatch"; Filename: "{app}\Wyrmwatch.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: CreateDesktopShortcut

[Run]
Filename: "{app}\Wyrmwatch.exe"; Description: "Open Wyrmwatch"; Flags: nowait postinstall skipifsilent unchecked; Check: CanOpenApplication

[Code]
var
  SupportExtracted, InstallComplete, InstallVerified, VerificationFailed: Boolean;
  RecoveryDirectory: String;

function GetCurrentProcessId: Cardinal;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

procedure ExtractSupport;
begin
  if not SupportExtracted then begin
    ExtractTemporaryFiles('{tmp}\support\*');
    SupportExtracted := True;
  end;
end;

function CreateStartMenuShortcut: Boolean;
begin Result := not FileExists(ExpandConstant('{group}\Wyrmwatch.lnk')); end;
function CreateDesktopShortcut: Boolean;
begin Result := not FileExists(ExpandConstant('{autodesktop}\Wyrmwatch.lnk')); end;
function CanOpenApplication: Boolean;
begin Result := InstallVerified and not VerificationFailed; end;

function RunHelper(Parameters: String): Boolean;
var Code: Integer; Diagnostic: AnsiString;
begin
  ExtractSupport;
  Result := Exec(ExpandConstant('{tmp}\support\Wyrmwatch.exe'), Parameters,
    ExpandConstant('{tmp}\support'), SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then begin
    Log('Installer helper exit status: ' + IntToStr(Code));
    if LoadStringFromFile(ExpandConstant('{tmp}\recovery-reference.txt.error.json'), Diagnostic) then
      Log('Installer helper diagnostic: ' + String(Diagnostic));
  end;
end;

function InitializeSetup: Boolean;
var Parent, Token: String;
begin
  Result := True;
  Parent := ExpandConstant('{param:UPDATEPARENT|}');
  Token := ExpandConstant('{param:UPDATEPARENTTOKEN|}');
  if Parent <> '' then
    Result := RunHelper('--installer-wait --parent ' + AddQuotes(Parent) + ' --parent-token ' + AddQuotes(Token));
end;

function InstallationBusy: Boolean;
var
  Locator, Services, Processes: Variant;
  AppPath, AgentPath: String;
begin
  AppPath := ExpandConstant('{app}\Wyrmwatch.exe');
  AgentPath := ExpandConstant('{app}\agent\Wyrmwatch.Agent.exe');
  StringChangeEx(AppPath, '\', '\\', True);
  StringChangeEx(AgentPath, '\', '\\', True);
  StringChangeEx(AppPath, '''', '\''', True);
  StringChangeEx(AgentPath, '''', '\''', True);
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE ExecutablePath=''' + AppPath + ''' OR ExecutablePath=''' + AgentPath + '''');
  Result := Processes.Count > 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Parameters: String; Reference: AnsiString;
begin
  Result := '';
  try
    if InstallationBusy then
      Result := 'Close Wyrmwatch and stop its background manager when idle before upgrading.'
    else if RecoveryDirectory = '' then begin
      Parameters := '--installer-prepare --root ' + AddQuotes(ExpandConstant('{app}')) +
        ' --parent ' + IntToStr(GetCurrentProcessId) + ' --reference ' + AddQuotes(ExpandConstant('{tmp}\recovery-reference.txt')) +
        ' --start-menu ' + AddQuotes(ExpandConstant('{group}\Wyrmwatch.lnk'));
      if ExpandConstant('{param:UPDATEWORKSPACE|}') <> '' then
        Parameters := Parameters + ' --workspace ' + AddQuotes(ExpandConstant('{param:UPDATEWORKSPACE|}'));
      if not RunHelper(Parameters) then
        Result := 'Could not create and verify an independent application recovery backup. Nothing was installed.'
      else if not LoadStringFromFile(ExpandConstant('{tmp}\recovery-reference.txt'), Reference) then
        Result := 'Could not read the application recovery reference. Nothing was installed.'
      else begin
        RecoveryDirectory := String(Reference);
        Log('Verified original application recovery directory: ' + RecoveryDirectory);
      end;
    end;
  except
    Result := 'Could not check for a running manager. Installation was stopped without closing any process.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
#ifdef FaultAfterFiles
    SaveStringToFile(RecoveryDirectory + '\fault-observed.txt', GetSHA256OfFile(ExpandConstant('{app}\README.md')), False);
    VerificationFailed := True;
    Log('Intentional fixture failure after application files were replaced.');
    Exit;
#endif
    InstallVerified := (RecoveryDirectory <> '') and RunHelper('--installer-verify --journal ' + AddQuotes(RecoveryDirectory));
    if not InstallVerified then begin
      VerificationFailed := True;
      Log('Installed application verification failed. The previous application will be restored after Setup exits.');
    end;
  end;
  if CurStep = ssDone then InstallComplete := InstallVerified and not VerificationFailed;
end;

function GetCustomSetupExitCode: Integer;
begin
  if VerificationFailed then Result := 1 else Result := 0;
end;

procedure DeinitializeSetup;
var Parameters, Helper: String; Code: Integer;
begin
  if RecoveryDirectory <> '' then begin
    Helper := RecoveryDirectory + '\support\Wyrmwatch.exe';
    Parameters := '--installer-finish --journal ' + AddQuotes(RecoveryDirectory);
    if InstallComplete then Parameters := Parameters + ' --success';
    if not Exec(Helper, Parameters, RecoveryDirectory + '\support', SW_HIDE, ewNoWait, Code) then
      Log('Could not start recovery completion. The verified original application backup is retained in the recovery directory.');
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  try
    Result := not InstallationBusy;
  except
    Result := False;
  end;
  if not Result then
    SuppressibleMsgBox('Close Wyrmwatch and stop its background manager when idle before uninstalling. Your servers and saved data will be preserved.', mbError, MB_OK, IDOK);
end;
