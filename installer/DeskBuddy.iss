; DeskBuddy 快启 - Inno Setup 安装脚本
; 用法：安装 Inno Setup 6 后，用 ISCC.exe 编译本文件：
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" DeskBuddy.iss

#define MyAppName "DeskBuddy 快启"
#define MyAppExeName "DeskBuddy.exe"
#define MyAppVersion "2.2.2"

[Setup]
AppId={{8F2E1C4A-5B7D-4E9A-9C3D-0A1B2C3D4E5F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=DeskBuddy
DefaultDirName={autopf}\DeskBuddy
DefaultGroupName=DeskBuddy 快启
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=DeskBuddy-Setup
SetupIconFile=..\src\DeskBuddy\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 免管理员，安装到当前用户目录
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "附加任务："

[Files]
Source: "..\dist\app\DeskBuddy.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\app\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DeskBuddy 快启"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\DeskBuddy 快启"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启（用户级，无管理员权限）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "DeskBuddy"; ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[UninstallDelete]
; 清理首次运行时生成的配置文件
Type: files; Name: "{app}\DeskBuddy.config.json"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 DeskBuddy 快启"; Flags: nowait postinstall skipifsilent
