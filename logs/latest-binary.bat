@echo off
title Binary Data Log Stream
echo ======================================
echo   Binary Data Log Stream
echo ======================================
echo.
echo Streaming latest binary data log...
echo Press Ctrl+C to stop
echo.

if not exist "binary/latest.log" (
    echo No latest.log file found in binary\ directory
    echo Make sure the service is running and binary logging is enabled.
    pause
    exit /b 1
)

echo Streaming: binary\latest.log
echo.

REM Stream the latest log file with PowerShell Get-Content -Wait
powershell -Command "Get-Content 'binary\latest.log' -Wait"

REM Exit cleanly without prompting
exit /b 0 
