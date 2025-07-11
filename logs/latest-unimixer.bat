@echo off
title UniMixer Service Log Stream
echo ======================================
echo   UniMixer Service Log Stream
echo ======================================
echo.
echo Streaming latest unimixer service log...
echo Press Ctrl+C to stop
echo.

if not exist "unimixer\latest.log" (
    echo No latest.log file found in unimixer\ directory
    echo Make sure the service is running and logs are being created.
    pause
    exit /b 1
)

echo Streaming: unimixer\latest.log
echo.

REM Stream the latest log file with PowerShell Get-Content -Wait
powershell -Command "Get-Content 'unimixer\latest.log' -Wait -Tail 20"

REM Exit cleanly without prompting
exit /b 0 
