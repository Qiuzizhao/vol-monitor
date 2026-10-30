@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 声域教师端——本机预览
echo 请在浏览器打开 http://127.0.0.1:5080。
echo 首次登录密码保存在 data\initial-password.txt。
echo 关闭此窗口将停止教师服务。
set "ASPNETCORE_URLS=http://127.0.0.1:5080"
Noise.Server.exe
echo 程序已停止，按任意键关闭窗口。
pause >nul
