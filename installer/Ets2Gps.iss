; Inno Setup script for ETS2-ATS GPS (Windows). Build with build-installer.ps1 (needs Inno Setup 6).
; Installs for the current user only (no administrator rights).
#define AppName "ETS2-ATS GPS"
#define AppVer "1.11.0"
#define OverlayExe "..\..\overlay-dotnet\publish\Ets2LaneOverlay.exe"

[Setup]
AppId={{7E3A1C52-5B8D-4E0F-A6C9-2F4D8B1E9A30}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher=ETS2-ATS GPS
DefaultDirName={localappdata}\Programs\ETS2 GPS
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\Ets2Gps.exe
OutputDir=Output
OutputBaseFilename=Ets2GpsSetup-{#AppVer}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=Ets2Gps.exe,Ets2LaneOverlay.exe
RestartApplications=no

[Types]
Name: "full"; Description: "Full installation (app and lane overlay window)"
Name: "compact"; Description: "App only"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "app"; Description: "ETS2-ATS GPS"; Types: full compact custom; Flags: fixed
#ifexist OverlayExe
Name: "overlay"; Description: "Lane overlay window (a small always-on-top lane panel; the app starts it for you)"; Types: full
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "autostart"; Description: "Start ETS2-ATS GPS when I log in to Windows"; Flags: unchecked

[Files]
Source: "..\publish\Ets2Gps.exe"; DestDir: "{app}"; Components: app; Flags: ignoreversion
#ifexist OverlayExe
Source: "{#OverlayExe}"; DestDir: "{localappdata}\Programs\ETS2 Lane Overlay"; Components: overlay; Flags: ignoreversion
#endif

[InstallDelete]
; the app was called "ETS2 GPS" before
Type: files; Name: "{autoprograms}\ETS2 GPS.lnk"
Type: files; Name: "{autodesktop}\ETS2 GPS.lnk"
Type: files; Name: "{userstartup}\ETS2 GPS.lnk"

[Icons]
Name: "{autoprograms}\ETS2-ATS GPS"; Filename: "{app}\Ets2Gps.exe"; Comment: "ETS2-ATS GPS"
Name: "{autodesktop}\ETS2-ATS GPS"; Filename: "{app}\Ets2Gps.exe"; Tasks: desktopicon
Name: "{userstartup}\ETS2-ATS GPS"; Filename: "{app}\Ets2Gps.exe"; Tasks: autostart

[Run]
Filename: "{app}\Ets2Gps.exe"; Description: "Start ETS2-ATS GPS now"; Flags: nowait postinstall skipifsilent

[Code]
// The map built from the game, saved trips and settings live in %APPDATA%\ETS2-GPS; keep them unless the user says otherwise.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    if MsgBox('Also delete your saved settings, trips and the map built from your game?' + #13#10 + ExpandConstant('{userappdata}\ETS2-GPS'),
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\ETS2-GPS'), True, True, True);
end;
