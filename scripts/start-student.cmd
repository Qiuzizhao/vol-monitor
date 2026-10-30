@echo off
chcp 65001 >nul
cd /d "%~dp0"
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0程序\launch-ui.ps1" -Target student
