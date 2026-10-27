@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-MediaServer.ps1"
if errorlevel 1 pause
