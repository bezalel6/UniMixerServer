@echo off
title UniMixer Split Log Viewer
echo ======================================
echo    UniMixer Split Log Viewer
echo ======================================
echo.
echo Starting split log viewer...
echo This will open multiple panes showing all logs simultaneously
echo.

REM Run the PowerShell script
powershell -ExecutionPolicy Bypass -File "watch-all-logs.ps1"

REM Exit cleanly without prompting
exit /b 0 
