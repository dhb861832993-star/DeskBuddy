@echo off
rem ============================================================
rem  QuickMenu 快启 - 便携版卸载脚本
rem ============================================================
chcp 65001 >nul
setlocal

set "DEST=%LOCALAPPDATA%\QuickMenu"

echo 正在退出 QuickMenu...
taskkill /f /im QuickMenu.exe >nul 2>&1

echo 删除开机自启...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v QuickMenu /f >nul 2>&1

echo 删除快捷方式...
del "%USERPROFILE%\Desktop\QuickMenu 快启.lnk" >nul 2>&1
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\QuickMenu 快启.lnk" >nul 2>&1

echo 删除程序目录...
rd /s /q "%DEST%" >nul 2>&1

echo.
echo 卸载完成！
pause
