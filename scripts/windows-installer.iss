#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef BuildDirectory
  #error BuildDirectory is required
#endif
#ifndef PackageDirectory
  #error PackageDirectory is required
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

[Files]
Source: "{#BuildDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\Wyrmwatch"; Filename: "{app}\Wyrmwatch.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Wyrmwatch"; Filename: "{app}\Wyrmwatch.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Wyrmwatch.exe"; Description: "Open Wyrmwatch"; Flags: nowait postinstall skipifsilent unchecked

[Code]
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
begin
  Result := '';
  try
    if InstallationBusy then
      Result := 'Close Wyrmwatch and stop its background manager when idle before upgrading.';
  except
    Result := 'Could not check for a running manager. Installation was stopped without closing any process.';
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
