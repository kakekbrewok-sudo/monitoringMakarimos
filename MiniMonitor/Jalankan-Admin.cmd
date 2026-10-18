@echo off
set "MINIMONITOR_EXE=%~dp0MiniMonitor.exe"
powershell.exe -NoProfile -Command "Start-Process -FilePath $env:MINIMONITOR_EXE -ArgumentList '--elevated' -Verb RunAs"
