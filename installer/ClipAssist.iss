#define MyAppName "ClipAssist"
#define MyAppVersion "0.1.0"
#define MyAppExeName "ClipAssist.exe"
#define MyReleaseDir "E:\ClipAssist-Release\v0.1.0"
#define MyOutputDir "E:\ClipAssist-Release"
#define MyProjectDir "E:\deepseekfile\ClipAssist"

[Setup]
AppId={{6E7D4D89-2C0F-4F45-9B51-8F63B6D72A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion=0.1.0.0
VersionInfoProductVersion=0.1.0
VersionInfoCompany=ClipAssist
VersionInfoCopyright=Copyright (C) 2026 ClipAssist. All rights reserved.
VersionInfoDescription=ClipAssist Setup
DefaultDirName={localappdata}\Programs\ClipAssist
DefaultGroupName=ClipAssist
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#MyOutputDir}
OutputBaseFilename=ClipAssist-0.1.0-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=E:\deepseekfile\ClipAssist\Assets\ClipAssist.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
AppCopyright=Copyright (C) 2026 ClipAssist. All rights reserved.
LicenseFile={#MyProjectDir}\LICENSE
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyReleaseDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyProjectDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyProjectDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyProjectDir}\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyProjectDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyProjectDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyProjectDir}\RELEASE-NOTES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ClipAssist"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\ClipAssist"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,ClipAssist}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: string;
  Expected: string;
begin
  // 只清理安装版自己的开机启动值，且只在该值确实指向本安装目录时删除。
  // 便携版使用 ClipAssistPortable，安装版使用 ClipAssist；这里绝不碰便携版值。
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'ClipAssist',
      Command) then
    begin
      Expected := ExpandConstant('{app}\{#MyAppExeName}');
      if Pos(Lowercase(Expected), Lowercase(Command)) > 0 then
      begin
        RegDeleteValue(
          HKCU,
          'Software\Microsoft\Windows\CurrentVersion\Run',
          'ClipAssist');
      end;
    end;
  end;
end;