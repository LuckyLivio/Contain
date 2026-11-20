@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-local-desktop.ps1" %*
if errorlevel 1 pause
