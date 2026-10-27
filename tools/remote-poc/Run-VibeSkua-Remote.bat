@echo off
title VibeSkua - Remote Flash (HP)
echo ================================================
echo   VibeSkua mode Remote: game jalan di HP
echo   (app "Ruffle Skua PoC"), bot jalan di PC ini.
echo ================================================
echo.
echo Lihat IP di notifikasi app HP, contoh: 192.168.1.23
set /p HPIP=Masukkan IP HP: 
if "%HPIP%"=="" (
  echo IP kosong, batal.
  pause
  exit /b 1
)
set SKUA_REMOTE_FLASH=%HPIP%

set SKUA_EXE=%~dp0VibeSkua.exe
if not exist "%SKUA_EXE%" (
  echo Tidak menemukan VibeSkua.exe di folder ini: %~dp0
  echo Taruh file .bat ini di folder yang sama dengan VibeSkua.exe.
  pause
  exit /b 1
)

echo Menjalankan VibeSkua, konek ke %HPIP%:5150 ...
start "" "%SKUA_EXE%"
