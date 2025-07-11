@echo off
title Incoming Data Log Stream
echo ======================================
echo   Incoming Data Log Stream
echo ======================================
echo.
echo Streaming latest incoming data log...
echo Press Ctrl+C to stop
echo.

if not exist "incoming\latest.log" (
    echo No latest.log file found in incoming\ directory
    echo Make sure the service is running and receiving data.
    pause
    exit /b 1
)

echo Streaming: incoming\latest.log
echo.

REM Stream the latest log file with PowerShell Get-Content -Wait
powershell -Command "Get-Content 'incoming\latest.log' -Wait -Tail 10"

REM Exit cleanly without prompting
exit /b 0 
