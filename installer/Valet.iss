; Valet — Inno Setup installer script
; See DESIGN.md §9 for what this is supposed to do.
;
; Build (from repo root):
;   .\build.ps1                  ; publishes Release exe AND compiles installer
;   -- or manually --
;   dotnet publish src/Valet/Valet.csproj -c Release -r win-x64 `
;     --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
;   iscc installer\Valet.iss
;
; Output: dist\Valet-Setup-<version>.exe

#define MyAppName      "Valet"
#define MyAppVersion   "0.1.0"
#define MyAppPublisher "Tandem Theory"
#define MyAppExeName   "Valet.exe"
#define PublishDir     "..\src\Valet\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
; Stable per-app GUID — must never change across versions (used for upgrade detection).
AppId={{B9E4A2D6-6F8C-4F1A-9F2B-7A1D8E5C3B40}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputDir=..\dist
OutputBaseFilename=Valet-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter=*.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";          Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Logon task with highest privileges so /sleep + future shutdown have the rights they need.
Filename: "schtasks.exe"; \
  Parameters: "/Create /TN ""{#MyAppName}"" /TR ""{app}\{#MyAppExeName}"" /SC ONLOGON /RL HIGHEST /F"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Registering logon task..."

; Inbound firewall rule for the HTTP server (LAN only, Private profile).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Remove-NetFirewallRule -DisplayName 'Valet HTTP 5009' -ErrorAction SilentlyContinue; New-NetFirewallRule -DisplayName 'Valet HTTP 5009' -Direction Inbound -Protocol TCP -LocalPort 5009 -Action Allow -Profile Private -RemoteAddress 192.168.69.0/24 | Out-Null"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Adding firewall rule..."

; URL ACL so the app can bind http://+:5009/ even when launched non-elevated.
Filename: "netsh.exe"; \
  Parameters: "http delete urlacl url=http://+:5009/"; \
  Flags: runhidden waituntilterminated
Filename: "netsh.exe"; \
  Parameters: "http add urlacl url=http://+:5009/ user=Everyone"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Reserving HTTP URL ACL..."

; Optional launch at end of install.
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch Valet now"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the running instance before removing files.
Filename: "taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "ValetKill"

; Remove the logon task. Tolerates "not found" via the unconditional /F.
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""{#MyAppName}"" /F"; Flags: runhidden; RunOnceId: "ValetTask"

; Remove the firewall rule (silent if absent).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Remove-NetFirewallRule -DisplayName 'Valet HTTP 5009' -ErrorAction SilentlyContinue"""; \
  Flags: runhidden; \
  RunOnceId: "ValetFw"

; Release the URL ACL.
Filename: "netsh.exe"; Parameters: "http delete urlacl url=http://+:5009/"; Flags: runhidden; RunOnceId: "ValetUrl"

; NOTE: %APPDATA%\Valet\ (config + logs + auth token) is intentionally preserved on uninstall.
; A reinstall picks up the same token so existing HA config keeps working.
