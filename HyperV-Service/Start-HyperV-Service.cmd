@echo off
rem Double-click this file to make sure the Hyper-V VM Management service (vmms) is running.
rem It will ask for administrator rights (UAC) and then start the service.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-HyperV-Service.ps1"
echo.
pause
