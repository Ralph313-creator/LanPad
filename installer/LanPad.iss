; LanPad installer (Inno Setup 6). Build it with:  powershell -File scripts\build-installer.ps1
; -> dist\LanPad-Setup-<version>.exe
;
; Does what install-image.ps1 does, in one exe:
;   app files to C:\LanPad (users may write there: lanpad.ini / games.user.ini live next to LanPad.exe),
;   ViGEmBus driver (if missing), firewall rules, desktop + Start menu shortcuts, and an uninstaller.
; CCBoot: run it once on the super-client (image write mode), then save the image.
; Unattended:  LanPad-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

#define Dist AddBackslash(SourcePath) + "..\dist\LanPad"
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(Dist + "\LanPad.exe")
#endif
#define ViGEmExe "ViGEmBus_1.22.0_x64_x86_arm64.exe"

[Setup]
AppId={{D0C8B206-9B06-4C19-8A5A-5739687366C6}
AppName=LanPad
AppVersion={#AppVersion}
AppVerName=LanPad {#AppVersion}
AppPublisher=RapTech
AppComments=Play local multiplayer games on different PCs over LAN
VersionInfoVersion={#AppVersion}
DefaultDirName=C:\LanPad
DefaultGroupName=LanPad
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\LanPad\LanPad.ico
UninstallDisplayIcon={app}\LanPad.exe
UninstallDisplayName=LanPad
Compression=lzma2/ultra64
SolidCompression=yes
; the compiler is 32-bit: compress in a separate 64-bit process, or ultra64 runs out of memory
LZMAUseSeparateProcess=yes
OutputDir=..\dist
OutputBaseFilename=LanPad-Setup-{#AppVersion}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "driver"; Description: "Install the virtual Xbox controller driver (ViGEmBus) - needed to host"; Check: NeedsViGEm
Name: "firewall"; Description: "Allow LanPad through Windows Firewall (UDP in)"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; LanPad saves its settings (lanpad.ini) and game exe paths (games.user.ini) next to LanPad.exe
Name: "{app}"; Permissions: users-modify

[Files]
Source: "{#Dist}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; \
  Excludes: "\lanpad.ini,\install-image.ps1,\driver,games.user.ini,selftest.log,*.pdb,\tools\mpv\doc,\tools\mpv\installer,\tools\mpv\*.bat"
; default settings only on a fresh install; an update keeps the cafe's settings
Source: "{#Dist}\lanpad.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#Dist}\driver\{#ViGEmExe}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Tasks: driver

[Icons]
Name: "{group}\LanPad"; Filename: "{app}\LanPad.exe"; WorkingDir: "{app}"; Comment: "LanPad - play local multiplayer games over LAN"
Name: "{group}\LanPad self-test"; Filename: "{app}\LanPad.exe"; Parameters: "--selftest"; WorkingDir: "{app}"
Name: "{group}\LanPad README"; Filename: "{app}\README.md"
Name: "{group}\{cm:UninstallProgram,LanPad}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\LanPad"; Filename: "{app}\LanPad.exe"; WorkingDir: "{app}"; Comment: "LanPad - play local multiplayer games over LAN"; Tasks: desktopicon

[Run]
Filename: "{tmp}\{#ViGEmExe}"; Parameters: "/quiet /norestart"; StatusMsg: "Installing the virtual Xbox controller driver (ViGEmBus)..."; Flags: waituntilterminated; Tasks: driver
; same rule names as install-image.ps1, so either one replaces the other's rules
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LanPad (host and join)"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LanPad stream viewer (mpv)"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LanPad stream viewer (ffplay)"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LanPad (host and join)"" dir=in action=allow protocol=UDP profile=any program=""{app}\LanPad.exe"""; StatusMsg: "Adding firewall rules..."; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LanPad stream viewer (mpv)"" dir=in action=allow protocol=UDP profile=any program=""{app}\tools\mpv\mpv.exe"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LanPad stream viewer (ffplay)"" dir=in action=allow protocol=UDP profile=any program=""{app}\tools\ffplay.exe"""; Flags: runhidden; Tasks: firewall
Filename: "{app}\LanPad.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,LanPad}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LanPad (host and join)"""; Flags: runhidden; RunOnceId: "DelFwHost"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LanPad stream viewer (mpv)"""; Flags: runhidden; RunOnceId: "DelFwMpv"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LanPad stream viewer (ffplay)"""; Flags: runhidden; RunOnceId: "DelFwFfplay"

[UninstallDelete]
Type: files; Name: "{app}\selftest.log"

[Code]
function NeedsViGEm: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\ViGEmBus');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('driver') and NeedsViGEm then
    SuppressibleMsgBox('The ViGEmBus driver did not install. Joined players will not get controllers on this PC ' +
      'until it is installed. Run this setup again, or install driver\{#ViGEmExe} by hand.',
      mbError, MB_OK, IDOK);
end;
