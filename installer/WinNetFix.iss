; WinNetFix Inno Setup 安装脚本
; 用法: ISCC.exe WinNetFix.iss /DMyAppVersion=0.1.2
; 产物: WinNetFix-Setup-<version>.exe

#define MyAppName "WinNetFix"
#define MyAppExeName "WinNetFix.exe"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "MuZiCul"
#define MyAppURL "https://github.com/MuZiCul/win-net"

[Setup]
AppId={{8E2C0B3A-2F4D-4C5A-9B1E-5D6A7B8C9D0E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 安装到 Program Files 需要管理员权限
PrivilegesRequired=admin
OutputDir=..\publish
OutputBaseFilename=WinNetFix-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 覆盖安装时不需要重启
CloseApplications=yes
RestartApplications=no
; 架构
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "..\installer\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "注册开机自启（登录时以最高权限运行）"; GroupDescription: "开机自启:"; Flags: checkedonce

[Files]
Source: "..\publish\WinNetFix.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; 安装后：注册计划任务自启（需管理员）
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install"; StatusMsg: "正在注册开机自启..."; Flags: runhidden; Tasks: autostart

[UninstallRun]
; 卸载前：移除计划任务
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall"; Flags: runhidden; RunOnceId: "RemoveTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// 安装完成页显示说明
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := 'WinNetFix {#MyAppVersion} 安装完成。' + #13#10 +
      '工具将常驻后台自动修复网络。' + #13#10 +
      '日志位置: %ProgramData%\WinNetFix\logs\';
end;
