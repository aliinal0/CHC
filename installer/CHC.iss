; CHC – Computer Health Check installer (Inno Setup 6.4 or newer).
; Build with installer\build.ps1, which publishes CHC.exe and passes the version.
;
; What it does besides copying CHC.exe:
;  - downloads and installs the .NET 10 Desktop Runtime and WebView2 only when they are missing,
;  - optionally installs the PawnIO driver (CPU temperature and motherboard sensors),
;  - optionally marks CHC to always run as administrator (Windows still asks at each start).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "CHC – Computer Health Check"
#define AppExe "CHC.exe"
#define DotNetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#define WebView2Url "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
; PawnIO is pinned to one release and checked against the SHA-256 GitHub lists for it, so a changed or
; replaced file is never run with administrator rights. Update both lines together for a new release.
#define PawnIOUrl "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"
#define PawnIOSha256 "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"

#ifdef UiTest
  #define RegRoot "HKCU"
#else
  #define RegRoot "HKLM"
#endif

[Setup]
AppId={{6F3B1C52-8E0A-4D4B-9C77-2E5A41C0B8D3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=aliinal0
AppPublisherURL=https://github.com/aliinal0/CHC
AppSupportURL=https://github.com/aliinal0/CHC/issues
AppUpdatesURL=https://github.com/aliinal0/CHC/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\CHC
DefaultGroupName=CHC
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
#ifdef UiTest
; Test build only (build without it for releases): installs per user so the wizard can be tried without UAC.
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\src\CHC.App\Assets\chc.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
CloseApplications=yes
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\publish
OutputBaseFilename=CHC-Setup-{#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
en.TaskAdmin=Always start CHC with administrator rights (disk health data, all sensors and FPS measurement; Windows asks for permission at each start)
en.TaskHistory=Keep a history in the background: start CHC in the notification area at sign-in (temperatures, usage and power over time; it stays on this PC)
en.TaskPawnIO=Install the PawnIO driver for CPU temperature and motherboard sensors (downloads about 3 MB)
en.GroupOptions=Options:
en.InstallingDotNet=Installing the .NET 10 Desktop Runtime...
en.InstallingWebView2=Installing Microsoft Edge WebView2 (used for PDF reports)...
en.InstallingPawnIO=Installing the PawnIO driver...
en.DotNetFailed=The .NET 10 Desktop Runtime could not be downloaded, and CHC needs it to start.%n%nCheck the internet connection and try again, or install it yourself from https://dotnet.microsoft.com/download/dotnet/10.0
en.OptionalFailed=An optional component could not be downloaded:%n%1%n%nCHC will be installed anyway; you can install it later.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "runasadmin"; Description: "{cm:TaskAdmin}"; GroupDescription: "{cm:GroupOptions}"
Name: "history"; Description: "{cm:TaskHistory}"; GroupDescription: "{cm:GroupOptions}"
Name: "pawnio"; Description: "{cm:TaskPawnIO}"; GroupDescription: "{cm:GroupOptions}"; Check: not IsPawnIOInstalled

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
; The Inter typeface is built into CHC.exe; its licence (SIL OFL 1.1) travels with it.
Source: "..\src\CHC.App\Assets\Fonts\Inter-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CHC"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\CHC"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The "Run this program as an administrator" compatibility setting; removed again on uninstall.
Root: {#RegRoot}; Subkey: "Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: string; ValueName: "{app}\{#AppExe}"; ValueData: "~ RUNASADMIN"; Tasks: runasadmin; Flags: uninsdeletevalue

[Run]
Filename: "{tmp}\windowsdesktop-runtime-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "{cm:InstallingDotNet}"; Check: Downloaded('windowsdesktop-runtime-win-x64.exe'); Flags: waituntilterminated
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:InstallingWebView2}"; Check: Downloaded('MicrosoftEdgeWebview2Setup.exe'); Flags: waituntilterminated
#ifndef UiTest
; (The per-user test build only downloads PawnIO: installing a driver needs administrator rights.)
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "{cm:InstallingPawnIO}"; Check: Downloaded('PawnIO_setup.exe'); Flags: waituntilterminated
#endif
#ifndef UiTest
; The sign-in task that keeps the history (or its removal when the box was unticked on an update).
Filename: "{app}\{#AppExe}"; Parameters: "--register-startup"; Tasks: history; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-startup"; Tasks: not history; Flags: runhidden waituntilterminated
#endif
; shellexec so that Windows can show its permission prompt when "run as administrator" was chosen.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,CHC}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""CHC\Background"" /F"; Flags: runhidden; RunOnceId: "RemoveBackgroundTask"

[Code]
const
  WebView2ClientKey = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  DownloadPage: TDownloadWizardPage;

function IsDotNetDesktop10Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then FindClose(FindRec);
end;

function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKLM32, 'SOFTWARE\' + WebView2ClientKey, 'pv', Version) or RegQueryStringValue(HKCU, 'Software\' + WebView2ClientKey, 'pv', Version))
    and (Version <> '') and (Version <> '0.0.0.0');
end;

function IsPawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

function Downloaded(FileName: String): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\') + FileName);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

{ Downloads one file; returns False (after logging) when it fails. With a SHA-256 a file that doesn't
  match is rejected and removed, so it can't be run. Microsoft's installers are evergreen and signed. }
function DownloadOne(Url, FileName, Sha256: String): Boolean;
begin
  DownloadPage.Clear;
  DownloadPage.Add(Url, FileName, Sha256);
  try
    DownloadPage.Download;
    Result := True;
  except
    Log(Format('Download of %s failed: %s', [FileName, GetExceptionMessage]));
    DeleteFile(ExpandConstant('{tmp}\') + FileName);
    Result := False;
  end;
end;

{ Fetches the missing prerequisites once the user presses Install. Without .NET CHC can't start,
  so that one stops the setup; WebView2 (PDF reports) and PawnIO (extra sensors) are optional. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedDotNet, NeedWebView2, NeedPawnIO: Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;

  NeedDotNet := not IsDotNetDesktop10Installed and not Downloaded('windowsdesktop-runtime-win-x64.exe');
  NeedWebView2 := not IsWebView2Installed and not Downloaded('MicrosoftEdgeWebview2Setup.exe');
  NeedPawnIO := WizardIsTaskSelected('pawnio') and not Downloaded('PawnIO_setup.exe');
  if not (NeedDotNet or NeedWebView2 or NeedPawnIO) then Exit;

  DownloadPage.Show;
  try
    if NeedDotNet and not DownloadOne('{#DotNetUrl}', 'windowsdesktop-runtime-win-x64.exe', '') then
    begin
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox(CustomMessage('DotNetFailed'), mbCriticalError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
    if NeedWebView2 and not DownloadOne('{#WebView2Url}', 'MicrosoftEdgeWebview2Setup.exe', '') then
    begin
      if DownloadPage.AbortedByUser then begin Result := False; Exit; end;
      SuppressibleMsgBox(FmtMessage(CustomMessage('OptionalFailed'), ['Microsoft Edge WebView2']), mbInformation, MB_OK, IDOK);
    end;
    if NeedPawnIO and not DownloadOne('{#PawnIOUrl}', 'PawnIO_setup.exe', '{#PawnIOSha256}') then
    begin
      if DownloadPage.AbortedByUser then begin Result := False; Exit; end;
      SuppressibleMsgBox(FmtMessage(CustomMessage('OptionalFailed'), ['PawnIO']), mbInformation, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;
