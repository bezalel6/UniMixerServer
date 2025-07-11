# UniMixer Log Streaming System

This directory contains organized logs and real-time streaming tools for the UniMixer server.

**The loggers themselves maintain this efficient structure** - each logger writes to both archived timestamped files and a live `latest.log` file for instant access.

## Quick Start

**Double-click `watch-logs.bat`** for an interactive menu to stream any log in real-time.

**🚀 NEW: Double-click `watch-all-logs.bat`** for split-screen viewing of ALL logs simultaneously!

## Log Structure

```
logs/
├── watch-logs.bat              # 🎯 MAIN LOG VIEWER (start here!)
├── watch-all-logs.bat          # 🚀 SPLIT VIEW - All logs at once!
├── watch-all-logs.ps1          # PowerShell split viewer
├── latest-unimixer.bat         # Stream service log
├── latest-incoming.bat         # Stream incoming data
├── latest-outgoing.bat         # Stream outgoing data  
├── latest-binary.bat           # Stream binary data
├──
├── unimixer/                   # Service logs (main events)
│   ├── latest.log              # 🔴 LIVE service log (always current)
│   └── unimixer-YYYYMMDD.log   # Archived daily logs
├── incoming/                   # Received messages
│   ├── latest.log              # 🔴 LIVE incoming data (always current)
│   └── incoming-data-YYYYMMDD.log  # Archived daily logs
├── outgoing/                   # Sent messages
│   ├── latest.log              # 🔴 LIVE outgoing data (always current)
│   └── outgoing-data-YYYYMMDD.log  # Archived daily logs
└── binary/                     # Raw binary protocol data
    ├── latest.log              # 🔴 LIVE binary stream (always current)
    └── binary-data-YYYYMMDD.log    # Archived daily logs
```

## 🚀 Split Log Viewer (NEW!)

**The split log viewer shows ALL logs simultaneously in a beautiful multi-pane layout:**

### Windows Terminal (Recommended)
- **4-pane split layout** with each log in its own section
- **Color-coded headers** for easy identification
- **Real-time streaming** of all logs at once
- **Automatic ESP32 detection** - incoming pane waits for data

### Fallback Mode 
- **Multiple PowerShell windows** if Windows Terminal unavailable
- **Same functionality** with separate windows instead of split panes

**Usage:**
```bash
# From logs directory:
.\watch-all-logs.bat          # Double-click or run from command line
.\watch-all-logs.ps1          # Direct PowerShell execution

# Or from the main menu:
.\watch-logs.bat → [0] SPLIT VIEW
```

## Log Types

### 🔧 Service Log (`unimixer/latest.log`)
- Main application events
- Error messages  
- Connection status
- Protocol statistics
- **Look here for**: Service startup issues, audio session problems, your 🔓 emoji messages

### 📥 Incoming Data (`incoming/latest.log`)
- All received messages (JSON)
- Message parsing results
- Protocol decode success/failures
- **Look here for**: Communication from ESP32, message format issues

### 📤 Outgoing Data (`outgoing/latest.log`)
- All sent messages (JSON/binary)
- Status broadcasts
- Asset responses
- **Look here for**: What the server is sending back

### 🔬 Binary Data (`binary/latest.log`)  
- Raw binary protocol stream
- Byte-level debugging
- Frame corruption analysis
- **Look here for**: Protocol corruption, ESP32 communication issues

## Efficient Design

Each logger maintains:
- **`latest.log`** - Always the current active log, reset on each service start
- **`*-YYYYMMDD.log`** - Daily archived logs for historical reference
- **Automatic rotation** - Old logs are cleaned up based on your config settings

No more searching for the latest timestamped file - the loggers handle this efficiently!

## Usage Examples

```bash
# BEST: Split view (all logs at once):
.\watch-all-logs.bat          # 🚀 Multi-pane real-time monitoring

# Individual streaming (double-click these files):
.\latest-unimixer.bat         # See service events (including 🔓 binary decode messages)
.\latest-incoming.bat         # See what ESP32 is sending  
.\latest-binary.bat           # See raw protocol data

# Direct file access:
Get-Content unimixer\latest.log -Wait    # Stream live service log
Get-Content incoming\latest.log -Wait    # Stream live incoming data
Get-Content binary\latest.log -Wait      # Stream live binary data
```

## Troubleshooting

**No logs appearing?**
- Check if the service is running
- Verify serial port connection
- Look in `unimixer\latest.log` for startup errors

**Can't see your 🔓 BINARY DECODE messages?**
- Use the **split viewer** to monitor all logs at once!
- Or run `.\latest-unimixer.bat` - warnings appear there
- Or run `.\latest-incoming.bat` - successful decodes appear there

**ESP32 not communicating?**
- Run `.\latest-binary.bat` to see raw data
- Check for protocol corruption or timing issues
- Use the split viewer to correlate binary data with parsing results

**Split viewer not working?**
- Install Windows Terminal for best experience
- Fallback mode uses multiple PowerShell windows automatically

**Performance Benefits:**
- No file searching required
- Instant access to current logs
- Automatic cleanup of old logs
- Each logger manages its own structure efficiently
- **Split view**: Monitor everything at once without switching windows!
