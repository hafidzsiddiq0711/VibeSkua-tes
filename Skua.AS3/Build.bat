@echo off
cd /d "%~dp0"
pwsh -ExecutionPolicy Bypass -File "%~dp0compile-as3.ps1"
exit /b %errorlevel%
