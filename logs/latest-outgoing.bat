@echo off
title Outgoing Data Log Stream
echo ======================================
echo   Outgoing Data Log Stream
echo ======================================
echo.
echo Streaming latest outgoing data log...
echo Press Ctrl+C to stop
echo.

if not exist "outgoing\latest.log" (
    echo No latest.log file found in outgoing\ directory
    echo Make sure the service is running and sending data.
    pause
    exit /b 1
)

echo Streaming: outgoing\latest.log
echo.

REM Stream the latest log file with PowerShell Get-Content -Wait
powershell -Command "Get-Content 'outgoing\latest.log' -Wait -Tail 10"

REM Exit cleanly without prompting
exit /b 0 
