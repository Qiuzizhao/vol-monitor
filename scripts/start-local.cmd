@echo off
cd /d "%~dp0"
echo RoomScope teacher workstation - local preview
echo Open http://127.0.0.1:5080 in your browser.
echo First-run password: data\initial-password.txt
echo Close this console to stop the teacher service.
set "ASPNETCORE_URLS=http://127.0.0.1:5080"
Noise.Server.exe
pause
