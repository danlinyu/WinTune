@echo off
REM WinTune launcher — uses Windows PowerShell 5.1 in STA mode.
REM Self-elevation is handled inside WinTune.ps1 (will trigger UAC).
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0WinTune.ps1"
