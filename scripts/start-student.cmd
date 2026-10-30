@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-student.ps1"
if errorlevel 1 (
  echo 启动失败，按任意键关闭窗口。
  pause >nul
)
