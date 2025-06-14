# UniMixer Server

A Windows audio control bridge system that enables remote control of Windows application audio levels through MQTT messaging or serial communication.

## Overview

UniMixer Server acts as a middleware service that translates network commands into Windows system audio control actions. It allows you to remotely control the volume of specific Windows applications (like media players, games, browsers) through standardized messaging protocols.

## Features

### 🎵 Audio Management

- **Real-time Audio Session Monitoring**: Continuously monitors volume levels of running Windows applications
- **Individual App Control**: Set specific volume levels for individual applications
- **Mute/Unmute Control**: Toggle mute state for specific applications
- **Status Broadcasting**: Periodically broadcasts current audio status

### 🌐 Multi-Protocol Communication

- **MQTT Protocol**: Home Assistant-compatible topic structure for network-based automation
- **Serial Communication**: Direct serial port communication for embedded systems
- **Extensible Architecture**: Easy to add new communication protocols

### ⚙️ Configuration & Deployment

- **JSON Configuration**: External configuration file for all settings
- **Windows Service Support**: Runs as a proper Windows background service
- **Structured Logging**: Comprehensive logging with Serilog
- **Auto-Reconnection**: Automatic reconnection for communication failures

## Quick Start

### Prerequisites

- Windows 10/11
- .NET 8.0 Runtime
- Administrator privileges (required for audio session control)

### Installation

1. **Download/Clone the project**
2. **Build the application**:
   ```bash
   dotnet build -c Release
   ```
3. **Run the application**:
   ```bash
   dotnet run
   ```

### Configuration

Edit `appsettings.json` to configure the application:

```json
{
  "DeviceId": "MyPC",
  "StatusBroadcastIntervalMs": 5000,
  "EnableMqtt": true,
  "EnableSerial": true,
  "Mqtt": {
    "BrokerHost": "192.168.1.100",
    "BrokerPort": 1883,
    "Username": "your_username",
    "Password": "your_password"
  },
  "Serial": {
    "PortName": "COM8",
    "BaudRate": 115200
  }
}
```

## Architecture

### Core Components

- **AudioManager**: Handles Windows Core Audio API interactions
- **Communication Handlers**: Abstract protocol implementations (MQTT, Serial)
- **UniMixerService**: Main orchestration service
- **Configuration System**: JSON-based configuration management

### File Structure

```
UniMixerServer/
├── Core/                    # Audio management core
│   ├── IAudioManager.cs     # Audio interface
│   ├── AudioManager.cs      # Core Audio API implementation
│   └── AudioSession.cs      # Audio session model
├── Communication/           # Protocol implementations
│   ├── ICommunicationHandler.cs  # Communication interface
│   ├── MqttHandler.cs            # MQTT implementation
│   └── SerialHandler.cs          # Serial implementation
├── Configuration/           # Configuration management
│   └── AppConfig.cs         # Configuration models
├── Models/                  # Data structures
│   └── AudioCommand.cs      # Command/response models
├── Services/               # Main service logic
│   └── UniMixerService.cs  # Service orchestration
└── Program.cs              # Application entry point
```

## MQTT Integration

### Topics Structure (Home Assistant Compatible)

- **Status Topic**: `homeassistant/unimix/audio_status`
  - Broadcasts current audio session status
- **Command Topic**: `homeassistant/unimix/audio/requests`
  - Receives control commands
- **Response Topic**: `homeassistant/unimix/audio/responses`
  - Sends command execution results

### MQTT Message Examples

**Status Message**:

```json
{
  "deviceId": "MyPC",
  "timestamp": "2024-01-15T10:30:00Z",
  "activeSessionCount": 3,
  "sessions": [
    {
      "processId": 1234,
      "processName": "chrome",
      "displayName": "Google Chrome",
      "volume": 0.75,
      "isMuted": false,
      "state": "Active"
    }
  ]
}
```

**Volume Control Command**:

```json
{
  "commandType": "SetVolume",
  "processId": 1234,
  "volume": 0.5,
  "requestId": "abc123"
}
```

**Mute Command**:

```json
{
  "commandType": "Mute",
  "processId": 1234,
  "requestId": "def456"
}
```

## Serial Communication

### Protocol Format

Commands are sent with prefix `CMD:` followed by JSON:

```
CMD:{"commandType":"SetVolume","processId":1234,"volume":0.5}
```

Status messages are sent with prefix `STATUS:`:

```
STATUS:{"deviceId":"MyPC","sessions":[...]}
```

Results are sent with prefix `RESULT:`:

```
RESULT:{"success":true,"message":"Volume set successfully"}
```

## Home Assistant Integration

### MQTT Discovery Configuration

Add to your Home Assistant configuration:

```yaml
mqtt:
  sensor:
    - name: "PC Audio Sessions"
      state_topic: "homeassistant/unimix/audio_status"
      value_template: "{{ value_json.activeSessionCount }}"
      json_attributes_topic: "homeassistant/unimix/audio_status"

  switch:
    - name: "Chrome Audio"
      command_topic: "homeassistant/unimix/audio/requests"
      payload_on: '{"commandType":"Unmute","processId":1234}'
      payload_off: '{"commandType":"Mute","processId":1234}'
```

## Advanced Usage

### Running as Windows Service

1. **Install as service**:

   ```bash
   sc create UniMixerServer binPath="C:\path\to\UniMixerServer.exe"
   ```

2. **Start the service**:
   ```bash
   sc start UniMixerServer
   ```

### Custom Communication Protocols

Implement `ICommunicationHandler` to add new protocols:

```csharp
public class MyCustomHandler : ICommunicationHandler
{
    public string Name => "MyProtocol";
    public bool IsConnected { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Your implementation
    }

    // ... implement other interface methods
}
```

### Arduino/ESP32 Integration

Example Arduino code for serial communication:

```cpp
void setVolume(int processId, float volume) {
    Serial.print("CMD:");
    Serial.print("{\"commandType\":\"SetVolume\",\"processId\":");
    Serial.print(processId);
    Serial.print(",\"volume\":");
    Serial.print(volume);
    Serial.println("}");
}
```

## Troubleshooting

### Common Issues

1. **"Access Denied" errors**: Run as Administrator
2. **No audio sessions found**: Ensure applications are playing audio
3. **MQTT connection fails**: Check broker settings and network connectivity
4. **Serial port errors**: Verify COM port availability and permissions

### Logging

Logs are written to:

- Console (when enabled)
- File: `logs/unimixer-YYYYMMDD-.log`

Adjust log levels in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": "Debug" // Information, Debug, Warning, Error
  }
}
```

## Development

### Building from Source

```bash
git clone <repository-url>
cd UniMixerServer
dotnet restore
dotnet build
```

### Running Tests

```bash
dotnet test
```

### Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

For issues and questions:

- Create an issue on GitHub
- Check the troubleshooting section
- Review the logs for error details

---

**Note**: This application requires administrator privileges to interact with Windows audio sessions. Always run with elevated permissions for full functionality.
