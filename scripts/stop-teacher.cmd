@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0程序\stop-teacher.ps1"
echo.
echo 按任意键关闭此窗口。
pause >nul
