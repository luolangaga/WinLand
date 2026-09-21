; ============================================================================
;  WinIsland 安装脚本（Inno Setup 6.5+）
;
;  每用户安装：默认装到 %LocalAppData%\Programs\WinIsland，全程不需要管理员权限。
;  插件目录是 {app}\plugins，安装目录必须对当前用户可写，所以这里用 lowest 权限。
;
;  本地编译（x64）：
;    & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
;        /DAppVersion=1.2.3 /DArch=x64 /DPublishDir=..\publish\x64 installer\WinIsland.iss
; ============================================================================

#define AppName      "WinIsland"
#define AppExeName   "WinIsland.exe"
#define AppPublisher "luolan"
#define AppUrl       "https://github.com/luolangaga/WinLand"
#define AppGuid      "{{F848ED0C-E295-440C-8BF6-264169B4DE5F}"

; 由 CI / 命令行覆盖，未指定时使用下列默认值
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef Arch
  #define Arch "x64"
#endif

#ifndef PublishDir
  #define PublishDir "..\WinIsland\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#endif

#if Arch == "arm64"
  #define ArchAllowed     "arm64"
  #define ArchInstallMode "arm64"
#elif Arch == "x64"
  ; x64 包不允许装到 Arm64 机器上（我们有原生 Arm64 包）
  #define ArchAllowed     "x64compatible and not arm64"
  #define ArchInstallMode "x64compatible"
#else
  #error Arch 只能是 "x64" 或 "arm64"
#endif

[Setup]
AppId={#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchInstallMode}
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename={#AppName}-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\WinIsland\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 自包含发布产物：.NET 运行时与 Windows App SDK 都已经在目录里，整目录拷贝即可
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,WinIsland}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; {app}\plugins 里的插件是 App 自己装的、不在这份安装清单里，卸载时一并清掉
Type: filesandordirs; Name: "{app}\plugins"
