@echo off
title VibeSkua - Remote (game di HP)
echo ==========================================================
echo   VibeSkua Remote: game jalan di HP, bot jalan di PC ini
echo ==========================================================
echo.
echo   1 = Artix Games Launcher  (DISARANKAN: lebih mulus + tombol mobile)
echo       Di HP: buka skua-mobile.swf pakai app Artix Games Launcher,
echo       lalu ketik IP PC ini.
echo.
echo   2 = App "Ruffle Skua PoC"
echo       Di HP: buka skua.swf pakai app Ruffle Skua PoC,
echo       lalu ketik IP HP di sini.
echo.
set MODE=1
set /p MODE=Pilih mode [1/2] (Enter = 1): 

if "%MODE%"=="2" goto :poc

:artix
set SKUA_REMOTE_FLASH=listen
echo.
echo ----------------------------------------------------------
echo  Ketik salah satu IP PC ini di HP (biasanya 192.168.x.x
echo  atau 172.20.10.x kalau pakai hotspot iPhone):
echo ----------------------------------------------------------
ipconfig | findstr /C:"IPv4"
echo.
echo  Kalau muncul pop-up Windows Firewall: centang Private DAN
echo  Public network, lalu klik "Allow access".
echo.
goto :run

:poc
echo.
echo Lihat IP di notifikasi app Ruffle Skua PoC, contoh: 192.168.1.23
set /p HPIP=Masukkan IP HP: 
if "%HPIP%"=="" (
  echo IP kosong, batal.
  pause
  exit /b 1
)
set SKUA_REMOTE_FLASH=%HPIP%

:run
set SKUA_EXE=%~dp0VibeSkua.exe
if not exist "%SKUA_EXE%" (
  echo Tidak menemukan VibeSkua.exe di folder ini: %~dp0
  echo Taruh file .bat ini di folder yang sama dengan VibeSkua.exe.
  pause
  exit /b 1
)
echo Menjalankan VibeSkua ...
start "" "%SKUA_EXE%"
echo.
echo Jendela ini boleh ditutup setelah VibeSkua terbuka.
pause
