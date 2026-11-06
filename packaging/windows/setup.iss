; rtltcp.server – Windows installer (Inno Setup 6).
; Built by packaging/windows/build-installer.ps1:  ISCC setup.iss /DPublishDir=<dir> /DAppVersion=<x.y.z>
;
; Fresh install: asks for the ports (web interface, first stick) and checks that they are free,
; stores them via "rtltcp-server setup", creates the Windows service and a firewall rule.
; Update: stops the service, replaces the files, starts it again - configuration stays untouched.
; Uninstall: removes service and firewall rule; data in %ProgramData%\rtltcp.server is kept.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif

#define ServiceName "rtltcp.server"
#define FirewallRule "rtltcp.server"
#define TrayRunValue "rtltcp.server Tray"

[Setup]
AppId={{B7E2B0A4-5C1E-4C55-9A7E-6E1F2A1D7C31}
AppName=rtltcp.server
AppVersion={#AppVersion}
AppVerName=rtltcp.server {#AppVersion}
AppPublisher=jaycloud
AppPublisherURL=https://jaycloud.at
AppSupportURL=https://github.com/JayLo1401/rtltcp.server
DefaultDirName={autopf}\rtltcp.server
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=output
OutputBaseFilename=setup
SetupIconFile=rtltcp.ico
UninstallDisplayIcon={app}\rtltcp-server.exe
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
CloseApplications=no
LicenseFile=..\..\LICENSE.md
VersionInfoVersion={#AppVersion}

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
de.PortsTitle=Ports
de.PortsSubtitle=Über welche Ports soll rtltcp.server erreichbar sein?
de.PortsInfo=Die Ports müssen frei sein. Weitere Sticks bekommen die folgenden Ports (1235, 1236 …). Alles lässt sich später in der Weboberfläche ändern.
de.WebPort=Port der Weboberfläche:
de.StreamPort=Port für den ersten Stick (rtl_tcp):
de.PortInvalid=Bitte gültige Ports (1–65535) eingeben, die sich unterscheiden.
de.PortBusy=Port %1 ist bereits belegt. Bitte einen anderen wählen.
de.OpenWeb=Weboberfläche öffnen
de.TrayTask=Tray-Symbol beim Anmelden anzeigen (Status, Weboberfläche, Neustart)
de.TrayIcon=rtltcp.server Tray-Symbol
de.Finished=rtltcp.server läuft als Windows-Dienst.%n%nWeboberfläche: http://localhost:%1/%nErste Anmeldung: admin / admin (danach eigenes Passwort festlegen).%n%nWichtig: Der RTL-SDR-Stick braucht einmalig den WinUSB-Treiber. Dazu Zadig (zadig.akeo.ie) starten, „Options → List All Devices“, den Stick („Bulk-In, Interface 0“ bzw. „RTL2832U“) wählen und „WinUSB“ installieren.
en.PortsTitle=Ports
en.PortsSubtitle=Which ports should rtltcp.server use?
en.PortsInfo=The ports must be free. Further sticks get the following ports (1235, 1236 …). Everything can be changed later in the web interface.
en.WebPort=Web interface port:
en.StreamPort=Port of the first stick (rtl_tcp):
en.PortInvalid=Please enter valid, different ports (1–65535).
en.PortBusy=Port %1 is already in use. Please choose another one.
en.OpenWeb=Open web interface
en.TrayTask=Show tray icon at logon (status, web interface, restart)
en.TrayIcon=rtltcp.server tray icon
en.Finished=rtltcp.server is running as a Windows service.%n%nWeb interface: http://localhost:%1/%nFirst login: admin / admin (you will be asked to set your own password).%n%nImportant: the RTL-SDR stick needs the WinUSB driver once. Run Zadig (zadig.akeo.ie), "Options → List All Devices", select the stick ("Bulk-In, Interface 0" or "RTL2832U") and install "WinUSB".

[Tasks]
Name: "tray"; Description: "{cm:TrayTask}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\..\LICENSE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{app}\rtltcp.server.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:{code:GetWebPort}/"

[Icons]
Name: "{autoprograms}\rtltcp.server"; Filename: "{app}\rtltcp.server.url"; IconFilename: "{app}\rtltcp-server.exe"
Name: "{autoprograms}\{cm:TrayIcon}"; Filename: "{app}\rtltcp-tray.exe"

; Tray icon for every user at logon (the task's checkbox decides; see CurStepChanged for unticking it later)
[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#TrayRunValue}"; ValueData: """{app}\rtltcp-tray.exe"""; Flags: uninsdeletevalue; Tasks: tray

[Run]
Filename: "{app}\rtltcp-tray.exe"; Flags: nowait runasoriginaluser skipifsilent; Tasks: tray
Filename: "http://localhost:{code:GetWebPort}/"; Description: "{cm:OpenWeb}"; Flags: postinstall shellexec nowait skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM rtltcp-tray.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopTray"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewall"

[UninstallDelete]
Type: files; Name: "{app}\rtltcp.server.url"
Type: files; Name: "{app}\rtltcp-tray.exe.old*"

[Code]
var
  PortPage: TInputQueryWizardPage;
  ExistingWebPort: String;
  FreshInstall: Boolean; // decided once at start; config.json exists after our own "setup" call

function DataDir(): String;
begin
  Result := ExpandConstant('{commonappdata}\rtltcp.server');
end;

function IsFreshInstall(): Boolean;
begin
  Result := FreshInstall;
end;

function Run(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function ServiceExists(): Boolean;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}') = 0;
end;

// Port of an existing installation (for the shortcut and the finish page on updates).
function ReadExistingWebPort(): String;
var
  Json: AnsiString;
  P, I: Integer;
begin
  Result := '8080';
  if not LoadStringFromFile(DataDir() + '\config.json', Json) then exit;
  P := Pos('"web"', Json);
  if P = 0 then exit;
  Delete(Json, 1, P);
  P := Pos('"port"', Json);
  if P = 0 then exit;
  Delete(Json, 1, P + 6);
  Result := '';
  for I := 1 to Length(Json) do
    if (Json[I] >= '0') and (Json[I] <= '9') then Result := Result + Json[I]
    else if Result <> '' then break;
  if Result = '' then Result := '8080';
end;

function GetWebPort(Param: String): String;
begin
  if IsFreshInstall() then Result := Trim(PortPage.Values[0])
  else Result := ExistingWebPort;
end;

function GetStreamPort(Param: String): String;
begin
  Result := Trim(PortPage.Values[1]);
end;

function PortIsFree(Port: String): Boolean;
begin
  Result := Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "try { $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, ' + Port +
    '); $l.Start(); $l.Stop(); exit 0 } catch { exit 1 }"') = 0;
end;

procedure InitializeWizard();
begin
  FreshInstall := not FileExists(DataDir() + '\config.json');
  ExistingWebPort := ReadExistingWebPort();
  PortPage := CreateInputQueryPage(wpSelectDir, CustomMessage('PortsTitle'), CustomMessage('PortsSubtitle'), CustomMessage('PortsInfo'));
  PortPage.Add(CustomMessage('WebPort'), False);
  PortPage.Add(CustomMessage('StreamPort'), False);
  PortPage.Values[0] := '8080';
  PortPage.Values[1] := '1234';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = PortPage.ID) and not IsFreshInstall();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Web, Stream: Integer;
begin
  Result := True;
  if CurPageID <> PortPage.ID then exit;
  Web := StrToIntDef(Trim(PortPage.Values[0]), 0);
  Stream := StrToIntDef(Trim(PortPage.Values[1]), 0);
  if (Web < 1) or (Web > 65535) or (Stream < 1) or (Stream > 65535) or (Web = Stream) then
  begin
    MsgBox(CustomMessage('PortInvalid'), mbError, MB_OK);
    Result := False;
  end
  else if not PortIsFree(IntToStr(Web)) then
  begin
    MsgBox(FmtMessage(CustomMessage('PortBusy'), [IntToStr(Web)]), mbError, MB_OK);
    Result := False;
  end
  else if not PortIsFree(IntToStr(Stream)) then
  begin
    MsgBox(FmtMessage(CustomMessage('PortBusy'), [IntToStr(Stream)]), mbError, MB_OK);
    Result := False;
  end;
end;

// A running tray icon (possibly in several user sessions) locks rtltcp-tray.exe. A running exe can be
// renamed though: move it aside, the new file goes into place, and every tray notices the new file and
// restarts itself from it (also after silent automatic updates, which can't start programs for users).
procedure MoveRunningTray();
var
  Exe, Old: String;
  I: Integer;
begin
  Exe := ExpandConstant('{app}\rtltcp-tray.exe');
  if not FileExists(Exe) then exit;
  for I := 0 to 9 do
  begin
    Old := Exe + '.old' + IntToStr(I);
    if FileExists(Old) then DeleteFile(Old); // fails while an older tray still runs from it
    if not FileExists(Old) then
    begin
      RenameFile(Exe, Old);
      exit;
    end;
  end;
end;

// Update: stop the running service before files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if ServiceExists() then Run(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  MoveRunningTray();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe: String;
begin
  if CurStep <> ssPostInstall then exit;
  Exe := ExpandConstant('{app}\rtltcp-server.exe');

  if IsFreshInstall() then
    Run(Exe, 'setup --data "' + DataDir() + '" --web-port ' + GetWebPort('') + ' --stream-port ' + GetStreamPort(''));

  if not ServiceExists() then
  begin
    Run(ExpandConstant('{sys}\sc.exe'), 'create {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto DisplayName= "rtltcp.server"');
    Run(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "rtl_tcp-Server für RTL-SDR-Sticks (Weboberfläche, rtltcp_v2)"');
    Run(ExpandConstant('{sys}\sc.exe'), 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000');
  end;

  // Firewall: allow the program (covers the web port and every stick port, also ones added later).
  // Private and domain networks only - a "public" network stays closed.
  Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"');
  Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#FirewallRule}" dir=in action=allow protocol=TCP program="' + Exe + '" enable=yes profile=private,domain');

  Run(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');

  // Tray checkbox unticked on a later update: [Registry] only adds, so remove the autostart here.
  if not WizardIsTaskSelected('tray') then
    RegDeleteValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#TrayRunValue}');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('Finished'), [GetWebPort('')]);
end;
