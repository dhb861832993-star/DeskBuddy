@echo off
rem ============================================================
rem  QuickMenu 快启 - 便携版安装脚本（免管理员）
rem  把程序复制到 %LOCALAPPDATA%\QuickMenu，
rem  创建开始菜单 + 桌面快捷方式，并注册开机自启。
rem ============================================================
chcp 65001 >nul
setlocal

set "SRC=%~dp0..\dist\app"
set "DEST=%LOCALAPPDATA%\QuickMenu"

echo [1/4] 复制程序文件...
if not exist "%SRC%\QuickMenu.exe" (
    echo 错误：找不到 %SRC%\QuickMenu.exe，请先运行 build.ps1 或下载完整发布包。
    pause
    exit /b 1
)
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%SRC%\QuickMenu.exe" "%DEST%\QuickMenu.exe" >nul
if exist "%SRC%\使用说明.txt" copy /y "%SRC%\使用说明.txt" "%DEST%\使用说明.txt" >nul

echo [2/4] 创建快捷方式...
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\QuickMenu 快启.lnk'); $lnk.TargetPath = '%DEST%\QuickMenu.exe'; $lnk.WorkingDirectory = '%DEST%'; $lnk.Save(); $sm = $ws.CreateShortcut([Environment]::GetFolderPath('Programs') + '\QuickMenu 快启.lnk'); $sm.TargetPath = '%DEST%\QuickMenu.exe'; $sm.WorkingDirectory = '%DEST%'; $sm.Save()"

echo [3/4] 注册开机自启...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v QuickMenu /t REG_SZ /d "\"%DEST%\QuickMenu.exe\"" /f >nul

echo [4/4] 启动 QuickMenu...
start "" "%DEST%\QuickMenu.exe"

echo.
echo 安装完成！已安装到：%DEST%
echo 提示：双击 Ctrl 呼出菜单；右键托盘图标可退出 / 编辑配置 / 取消自启。
pause
