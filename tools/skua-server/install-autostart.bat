@echo off
rem Run Skua Server when this user logs in, and let other devices (Tailscale) reach port 6100.
rem Right-click -> Run as administrator.
cd /d "%~dp0"
schtasks /Create /TN "Skua Server" /TR "\"%~dp0start-skua-server.bat\"" /SC ONLOGON /RL HIGHEST /F
netsh advfirewall firewall delete rule name="Skua Server" >nul 2>&1
netsh advfirewall firewall add rule name="Skua Server" dir=in action=allow protocol=TCP localport=6100
echo.
echo Done. Skua Server now starts when you log in. Starting it now ...
start "" "%~dp0start-skua-server.bat"
pause
