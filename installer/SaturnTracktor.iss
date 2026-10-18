#define AppVersion "1.0.2"

[Setup]
AppId={{5D54E52C-8D91-4999-AD93-28F0E7C5D892}
AppName=Saturn Tracktor
AppVersion={#AppVersion}
AppPublisher=Gevork Saturn
AppPublisherURL=https://github.com/manoilovic
AppSupportURL=https://github.com/manoilovic/saturn-tracktor/issues
DefaultDirName={autopf}\SaturnTracktor
DefaultGroupName=Saturn Tracktor
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
ShowLanguageDialog=no
WizardStyle=modern dynamic
SetupIconFile=payload\Saturn.ico
UninstallDisplayIcon={app}\SaturnTracktor.exe
UninstallDisplayName=Saturn Tracktor
VersionInfoCompany=Gevork Saturn
VersionInfoCopyright=Copyright (C) 2026 Gevork Saturn
VersionInfoDescription=Saturn Tracktor Installer
VersionInfoProductName=Saturn Tracktor
VersionInfoVersion={#AppVersion}.0
OutputDir=output
OutputBaseFilename=SaturnTracktor-Setup-{#AppVersion}
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
AppMutex=Local\SaturnTracktor-v1
LicenseFile=payload\LICENSE.txt

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "payload\SaturnTracktor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\SaturnTracktor-source.zip"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\Saturn Tracktor"; Filename: "{app}\SaturnTracktor.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Saturn Tracktor"; Filename: "{app}\SaturnTracktor.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\SaturnTracktor.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent; Check: CanLaunch

[CustomMessages]
russian.LaunchApp=Запустить Saturn Tracktor
english.LaunchApp=Launch Saturn Tracktor
russian.WebViewStatus=Подготовка интерфейса Microsoft WebView2...
english.WebViewStatus=Preparing Microsoft WebView2...
russian.AudioStatus=Настройка системного аудиокомпонента...
english.AudioStatus=Configuring the system audio component...
russian.WebViewWarning=Не удалось установить Microsoft WebView2. Проверьте подключение к Интернету и запустите установщик Saturn Tracktor ещё раз.
english.WebViewWarning=Microsoft WebView2 could not be installed. Check your Internet connection and run Saturn Tracktor Setup again.
russian.AudioWarning=Приложение установлено, но настройка аудиокомпонента не завершена. Запустите Saturn Tracktor и нажмите «Настроить звук».
english.AudioWarning=The app is installed, but audio setup did not complete. Open Saturn Tracktor and click Configure audio.
russian.RestartMessage=Аудиокомпонент установлен. Перезагрузите Windows перед включением обработки звука.
english.RestartMessage=The audio component is installed. Restart Windows before enabling audio processing.

[Code]
var
  AudioNeedsRestart: Boolean;
  SetupProblem: String;

function ValidRuntime(Root: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root,
    'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function RuntimeInstalled: Boolean;
begin
  Result := ValidRuntime(HKLM32) or ValidRuntime(HKLM64) or ValidRuntime(HKCU);
end;

procedure EnsureRuntime;
var
  ResultCode: Integer;
  Started: Boolean;
begin
  if RuntimeInstalled then begin
    Log('WebView2 is already installed.');
    Exit;
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('WebViewStatus');
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  Started := Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'),
    '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('WebView2 installer started=%d exit=%d', [Ord(Started), ResultCode]));
  if not RuntimeInstalled then SetupProblem := CustomMessage('WebViewWarning');
end;

procedure EnsureAudio;
var
  ResultCode: Integer;
  DevicePath: String;
  DeviceJson: AnsiString;
  Started: Boolean;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('AudioStatus');
  DevicePath := ExpandConstant('{tmp}\SaturnTracktor-devices.json');
  Started := Exec(ExpandConstant('{app}\SaturnTracktor.exe'),
    '--devices "' + DevicePath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Started and (ResultCode = 0) and LoadStringFromFile(DevicePath, DeviceJson) then
    if Pos('"IsVirtual": true', String(DeviceJson)) > 0 then begin
      Log('Active virtual audio device found; driver installation skipped.');
      if not Exec(ExpandConstant('{app}\SaturnTracktor.exe'), '--rename-device',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log('Could not set the virtual device display name.');
      Exit;
    end;
  Started := ShellExec('runas', ExpandConstant('{app}\SaturnTracktor.exe'),
    '--install-driver', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('Audio installer started=%d exit=%d', [Ord(Started), ResultCode]));
  if Started and (ResultCode = 0) then Exit;
  if Started and (ResultCode = 1) then begin
    AudioNeedsRestart := True;
    Exit;
  end;
  if SetupProblem <> '' then SetupProblem := SetupProblem + #13#10;
  SetupProblem := SetupProblem + CustomMessage('AudioWarning');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    EnsureRuntime;
    EnsureAudio;
    if SetupProblem <> '' then begin
      Log(SetupProblem);
      SuppressibleMsgBox(SetupProblem, mbError, MB_OK, IDOK);
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then begin
    if SetupProblem <> '' then
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + SetupProblem;
    if AudioNeedsRestart then
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('RestartMessage');
  end;
end;

function NeedRestart: Boolean;
begin
  Result := AudioNeedsRestart;
end;

function CanLaunch: Boolean;
begin
  Result := (SetupProblem = '') and not AudioNeedsRestart;
end;
