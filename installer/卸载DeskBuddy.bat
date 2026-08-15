@echo off
rem ============================================================
rem  DeskBuddy 快启 - 便携版卸载脚本
rem ============================================================
chcp 65001 >nul
setlocal

set "DEST=%LOCALAPPDATA%\DeskBuddy"

echo 正在退出 DeskBuddy...
taskkill /f /im DeskBuddy.exe >nul 2>&1

echo 删除开机自启...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DeskBuddy /f >nul 2>&1

echo 删除快捷方式...
del "%USERPROFILE%\Desktop\DeskBuddy 快启.lnk" >nul 2>&1
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\DeskBuddy 快启.lnk" >nul 2>&1

echo 删除程序目录...
rd /s /q "%DEST%" >nul 2>&1

echo.
echo 卸载完成！
pause
