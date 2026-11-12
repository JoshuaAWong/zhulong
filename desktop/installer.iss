; 独立用户级安装；编译输入仅为build.py生成的同一运行产物。
#ifndef RuntimeDir
  #define RuntimeDir "..\dist\Zhulong"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef AppVersion
  #define AppVersion "0.2.5-replacement"
#endif

[Setup]
AppId={{4332A2F6-91D8-4441-897C-77A711B7E568}
AppName=烛龙新版
AppVersion={#AppVersion}
AppVerName=烛龙新版 {#AppVersion}
AppPublisher=Zhulong
DefaultDirName={localappdata}\Programs\Zhulong
DefaultGroupName=烛龙新版
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=Local\ZhulongNextInstance
SetupMutex=ZhulongNextSetup-4332A2F6-91D8-4441-897C-77A711B7E568
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes
SetupIconFile={#RuntimeDir}\Assets\zhulong-next.ico
UninstallDisplayIcon={app}\Assets\zhulong-next.ico
UninstallDisplayName=烛龙新版
OutputDir={#OutputDir}
OutputBaseFilename=Zhulong-{#AppVersion}-setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion=0.2.0.0

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
; 数据、更新暂存、源码及调试文件不进入安装包；安装标记由PostInstall单独生成。
Source: "{#RuntimeDir}\*"; DestDir: "{app}"; Excludes: "\data,\data\*,\updates,\updates\*,\installation.json,*.cs,*.csproj,*.sln,*.py,*.pyc,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\烛龙新版"; Filename: "{app}\Zhulong.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\zhulong-next.ico"
Name: "{autodesktop}\烛龙新版"; Filename: "{app}\Zhulong.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\zhulong-next.ico"; Tasks: desktopicon
Name: "{group}\卸载烛龙新版"; Filename: "{uninstallexe}"

[UninstallDelete]
; 只删除本安装器生成的形态标记，data/updates始终保留。
Type: files; Name: "{app}\installation.json"

#ifdef EnableOwnedTaskCleanup
[UninstallRun]
; 仅在程序已实现并验证该参数后显式启用，绝不按通用任务名删除。
Filename: "{app}\Zhulong.exe"; Parameters: "--uninstall-tasks"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupZhulongOwnedTasks"
#endif

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not SaveStringToFile(ExpandConstant('{app}\installation.json'),
      '{"mode":"installed","version":"{#AppVersion}"}', False) then
      RaiseException('Cannot write installation.json.');
  end;
end;
