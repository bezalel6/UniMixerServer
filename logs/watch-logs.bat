@echo off
title UniMixer Log Viewer
color 0A

:MENU
cls
echo ==========================================
echo        UniMixer Log Stream Viewer
echo ==========================================
echo.
echo Select a log stream to watch:
echo.
echo [1] UniMixer Service Log  (main service events)
echo [2] Incoming Data Log     (received messages)  
echo [3] Outgoing Data Log     (sent messages)
echo [4] Binary Data Log       (raw binary stream)
echo.
echo [0] 🚀 SPLIT VIEW - Watch ALL logs simultaneously!
echo.
echo [5] Show all log files
echo [6] Open logs directory
echo.
echo [Q] Quit
echo.
set /p choice="Enter your choice (0-6, Q): "

if /i "%choice%"=="0" goto SPLIT
if /i "%choice%"=="1" goto SERVICE
if /i "%choice%"=="2" goto INCOMING  
if /i "%choice%"=="3" goto OUTGOING
if /i "%choice%"=="4" goto BINARY
if /i "%choice%"=="5" goto LISTLOGS
if /i "%choice%"=="6" goto OPENDIR
if /i "%choice%"=="Q" goto EXIT

echo Invalid choice. Please try again.
pause
goto MENU

:SERVICE
echo Starting UniMixer Service Log stream...
call latest-unimixer.bat
goto MENU

:INCOMING
echo Starting Incoming Data Log stream...
call latest-incoming.bat
goto MENU

:OUTGOING  
echo Starting Outgoing Data Log stream...
call latest-outgoing.bat
goto MENU

:BINARY
echo Starting Binary Data Log stream...
call latest-binary.bat
goto MENU

:LISTLOGS
cls
echo ==========================================
echo           Log File Structure
echo ==========================================
echo.
echo === LIVE LOGS (Always Current) ===
if exist "unimixer\latest.log" (
    echo ✅ unimixer\latest.log
) else (
    echo ❌ unimixer\latest.log (service not running)
)

if exist "incoming\latest.log" (
    echo ✅ incoming\latest.log  
) else (
    echo ❌ incoming\latest.log (no incoming data)
)

if exist "outgoing\latest.log" (
    echo ✅ outgoing\latest.log
) else (
    echo ❌ outgoing\latest.log (no outgoing data)
)

if exist "binary\latest.log" (
    echo ✅ binary\latest.log
) else (
    echo ❌ binary\latest.log (binary logging disabled)
)

echo.
echo === ARCHIVED LOGS (Historical) ===
echo Service Logs:
dir /b unimixer\unimixer*.log 2>nul | findstr /v "latest.log"
echo.
echo Incoming Data:
dir /b incoming\incoming*.log 2>nul | findstr /v "latest.log"
echo.
echo Outgoing Data:
dir /b outgoing\outgoing*.log 2>nul | findstr /v "latest.log"
echo.
echo Binary Data:
dir /b binary\binary*.log 2>nul | findstr /v "latest.log"
echo.
pause
goto MENU

:OPENDIR
explorer .
goto MENU

:EXIT
echo Goodbye!
exit /b 0 

:SPLIT
echo Starting Split Log Viewer...
call watch-all-logs.bat
goto MENU 
