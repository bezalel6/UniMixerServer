# UniMixer Split Log Viewer
# Opens Windows Terminal with split panes monitoring all log streams simultaneously

param(
    [switch]$UseWindowsTerminal = $true,
    [switch]$UsePowerShell = $false
)

Write-Host "🚀 UniMixer Split Log Viewer" -ForegroundColor Green
Write-Host "============================" -ForegroundColor Green
Write-Host ""

# Check if we're in the logs directory
if (!(Test-Path "unimixer" -PathType Container)) {
    Write-Host "❌ Error: Please run this script from the logs directory" -ForegroundColor Red
    Write-Host "Current location: $(Get-Location)" -ForegroundColor Yellow
    Write-Host "Expected to find: unimixer/, incoming/, outgoing/, binary/ subdirectories" -ForegroundColor Yellow
    pause
    exit 1
}

# Check which log files exist
$logStatus = @{
    "Service"  = Test-Path "unimixer\latest.log"
    "Incoming" = Test-Path "incoming\latest.log" 
    "Outgoing" = Test-Path "outgoing\latest.log"
    "Binary"   = Test-Path "binary\latest.log"
}

Write-Host "📊 Log File Status:" -ForegroundColor Cyan
foreach ($log in $logStatus.GetEnumerator()) {
    $status = if ($log.Value) { "✅" } else { "❌" }
    $color = if ($log.Value) { "Green" } else { "Red" }
    Write-Host "  $status $($log.Key): $($log.Value)" -ForegroundColor $color
}
Write-Host ""

$useWindowsTerminal = $UseWindowsTerminal -and (Get-Command "wt.exe" -ErrorAction SilentlyContinue) -and !$UsePowerShell

if ($useWindowsTerminal) {
    Write-Host "🖥️  Opening Windows Terminal with split panes..." -ForegroundColor Green
    
    # Build the Windows Terminal command with proper escaping
    $wtArgs = @()
    
    # Start with the service log (main pane)
    $wtArgs += "--title", "🔧 UniMixer Service"
    $wtArgs += "powershell", "-NoExit", "-Command"
    $wtArgs += "Write-Host '🔧 UniMixer Service Log' -ForegroundColor Green; Write-Host '===================' -ForegroundColor Green; Write-Host ''; if (Test-Path 'unimixer\latest.log') { Get-Content 'unimixer\latest.log' -Wait -Tail 10 } else { Write-Host 'No service log found - start the service first' -ForegroundColor Yellow; pause }"
    
    # Split horizontally for incoming data
    $wtArgs += ";"
    $wtArgs += "split-pane", "--horizontal", "--title", "📥 Incoming Data"
    $wtArgs += "powershell", "-NoExit", "-Command"
    if ($logStatus.Incoming) {
        $wtArgs += "Write-Host '📥 Incoming Data Log' -ForegroundColor Blue; Write-Host '==================' -ForegroundColor Blue; Write-Host ''; Get-Content 'incoming\latest.log' -Wait -Tail 5"
    } else {
        $wtArgs += "Write-Host '📥 Incoming Data Log (Waiting for ESP32...)' -ForegroundColor Yellow; Write-Host '========================================' -ForegroundColor Yellow; Write-Host ''; while (!(Test-Path 'incoming\latest.log')) { Start-Sleep 2 }; Get-Content 'incoming\latest.log' -Wait -Tail 5"
    }
    
    # Split the first pane vertically for outgoing data  
    $wtArgs += ";"
    $wtArgs += "move-focus", "up"
    $wtArgs += ";"
    $wtArgs += "split-pane", "--vertical", "--title", "📤 Outgoing Data"
    $wtArgs += "powershell", "-NoExit", "-Command"
    $wtArgs += "Write-Host '📤 Outgoing Data Log' -ForegroundColor Magenta; Write-Host '==================' -ForegroundColor Magenta; Write-Host ''; if (Test-Path 'outgoing\latest.log') { Get-Content 'outgoing\latest.log' -Wait -Tail 5 } else { Write-Host 'No outgoing log found' -ForegroundColor Yellow; pause }"
    
    # Split the second pane vertically for binary data
    $wtArgs += ";"
    $wtArgs += "move-focus", "down"
    $wtArgs += ";"
    $wtArgs += "split-pane", "--vertical", "--title", "🔬 Binary Data"
    $wtArgs += "powershell", "-NoExit", "-Command"
    $wtArgs += "Write-Host '🔬 Binary Data Log' -ForegroundColor Cyan; Write-Host '=================' -ForegroundColor Cyan; Write-Host ''; if (Test-Path 'binary\latest.log') { Get-Content 'binary\latest.log' -Wait -Tail 8 } else { Write-Host 'No binary log found' -ForegroundColor Yellow; pause }"
    
    # Execute the command
    try {
        & "wt.exe" $wtArgs
        Write-Host "✅ Windows Terminal opened with split panes" -ForegroundColor Green
        Write-Host "📌 Each pane will auto-refresh when logs are updated" -ForegroundColor Gray
    } catch {
        Write-Host "❌ Failed to open Windows Terminal: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "🔄 Falling back to multiple PowerShell windows..." -ForegroundColor Yellow
        $useWindowsTerminal = $false
    }
}

if (!$useWindowsTerminal) {
    Write-Host "⚠️  Windows Terminal not available or disabled" -ForegroundColor Yellow
    Write-Host "📱 Opening multiple PowerShell windows instead..." -ForegroundColor Yellow
    Write-Host ""
    
    # Create individual PowerShell windows for each log
    $jobs = @()
    
    # Service Log Window
    $serviceJob = Start-Process powershell -ArgumentList @(
        "-NoExit", 
        "-Command", 
        "Set-Location '$pwd'; Write-Host '🔧 UniMixer Service Log' -ForegroundColor Green; Write-Host '=======================' -ForegroundColor Green; Write-Host ''; if (Test-Path 'unimixer\latest.log') { Get-Content 'unimixer\latest.log' -Wait -Tail 10 } else { Write-Host 'No service log found - start the service first' -ForegroundColor Yellow; pause }"
    ) -PassThru -WindowStyle Normal
    
    Start-Sleep 1
    
    # Outgoing Log Window  
    if ($logStatus.Outgoing) {
        $outgoingJob = Start-Process powershell -ArgumentList @(
            "-NoExit",
            "-Command", 
            "Set-Location '$pwd'; Write-Host '📤 Outgoing Data Log' -ForegroundColor Magenta; Write-Host '====================' -ForegroundColor Magenta; Write-Host ''; Get-Content 'outgoing\latest.log' -Wait -Tail 5"
        ) -PassThru -WindowStyle Normal
        Start-Sleep 1
    }
    
    # Incoming Log Window (if exists or waiting)
    if ($logStatus.Incoming) {
        $incomingJob = Start-Process powershell -ArgumentList @(
            "-NoExit",
            "-Command", 
            "Set-Location '$pwd'; Write-Host '📥 Incoming Data Log' -ForegroundColor Blue; Write-Host '====================' -ForegroundColor Blue; Write-Host ''; Get-Content 'incoming\latest.log' -Wait -Tail 5"
        ) -PassThru -WindowStyle Normal
    } else {
        $incomingJob = Start-Process powershell -ArgumentList @(
            "-NoExit",
            "-Command", 
            "Set-Location '$pwd'; Write-Host '📥 Incoming Data Log (Waiting for ESP32...)' -ForegroundColor Yellow; Write-Host '==========================================' -ForegroundColor Yellow; Write-Host ''; while (!(Test-Path 'incoming\latest.log')) { Write-Host 'Waiting for ESP32 data...' -ForegroundColor Gray; Start-Sleep 2 }; Write-Host 'ESP32 data detected! Starting stream...' -ForegroundColor Green; Get-Content 'incoming\latest.log' -Wait -Tail 5"
        ) -PassThru -WindowStyle Normal
    }
    Start-Sleep 1
    
    # Binary Log Window
    if ($logStatus.Binary) {
        $binaryJob = Start-Process powershell -ArgumentList @(
            "-NoExit",
            "-Command", 
            "Set-Location '$pwd'; Write-Host '🔬 Binary Data Log' -ForegroundColor Cyan; Write-Host '==================' -ForegroundColor Cyan; Write-Host ''; Get-Content 'binary\latest.log' -Wait -Tail 8"
        ) -PassThru -WindowStyle Normal
    }
    
    Write-Host "✅ Opened multiple PowerShell windows for log monitoring" -ForegroundColor Green
    Write-Host "📌 Close the windows when you're done monitoring" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "💡 Tips:" -ForegroundColor Yellow
Write-Host "  • Start your UniMixer service (dotnet run) to see live logs" -ForegroundColor Gray
Write-Host "  • Connect your ESP32 to see incoming data stream" -ForegroundColor Gray  
Write-Host "  • Look for 🔓 BINARY DECODE SUCCESS messages in the service log" -ForegroundColor Gray
Write-Host "  • Press Ctrl+C in any pane to stop that stream" -ForegroundColor Gray
Write-Host "" 
