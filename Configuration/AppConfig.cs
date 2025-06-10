using System;
using System.Collections.Generic;

namespace UniMixerServer.Configuration
{
    public class AppConfig
    {
        public string DeviceId { get; set; } = Environment.MachineName;
        public int StatusBroadcastIntervalMs { get; set; } = 10000;
        public int AudioSessionRefreshIntervalMs { get; set; } = 5000;
        public bool EnableMqtt { get; set; } = true;
        public bool EnableSerial { get; set; } = true;
        public MqttConfig Mqtt { get; set; } = new MqttConfig();
        public SerialConfig Serial { get; set; } = new SerialConfig();
        public LoggingConfig Logging { get; set; } = new LoggingConfig();
        public AudioConfig Audio { get; set; } = new AudioConfig();

        /// <summary>
        /// List of allowed process names or regex patterns. If empty, all processes are allowed.
        /// Can contain exact process names (e.g., "spotify.exe") or regex patterns (e.g., ".*music.*")
        /// </summary>
        public List<string> AllowedProcesses { get; set; } = new List<string> { "chrome", "Legcord", "Youtube Music" };
    }

    public class AudioConfig
    {
        /// <summary>
        /// Whether to include all audio devices or just the default device
        /// </summary>
        public bool IncludeAllDevices { get; set; } = false;

        /// <summary>
        /// Whether to include capture (input) devices in addition to render (output) devices
        /// </summary>
        public bool IncludeCaptureDevices { get; set; } = false;

        /// <summary>
        /// The data flow to monitor (Render=playback, Capture=recording, All=both)
        /// </summary>
        public string DataFlow { get; set; } = "Render";

        /// <summary>
        /// The device role to use (Console=default, Multimedia=multimedia apps, Communications=voice apps)
        /// </summary>
        public string DeviceRole { get; set; } = "Console";

        /// <summary>
        /// Whether to enable detailed logging for audio operations
        /// </summary>
        public bool EnableDetailedLogging { get; set; } = true;
    }

    public class MqttConfig
    {
        public string BrokerHost { get; set; } = "localhost";
        public int BrokerPort { get; set; } = 1883;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ClientId { get; set; } = $"unimixer-{Environment.MachineName}";
        public bool UseTls { get; set; } = false;
        public MqttTopics Topics { get; set; } = new MqttTopics();
        public int ReconnectDelayMs { get; set; } = 5000;
        public int KeepAliveIntervalMs { get; set; } = 60000;
    }

    public class MqttTopics
    {
        public string StatusTopic { get; set; } = "homeassistant/unimix/audio_status";
        public string CommandTopic { get; set; } = "homeassistant/unimix/audio/requests";
        public string ResponseTopic { get; set; } = "homeassistant/unimix/audio/responses";
        public string ControlTopic { get; set; } = "homeassistant/unimix/audio/control";
        public string DiscoveryPrefix { get; set; } = "homeassistant";
    }

    public class SerialConfig
    {
        public string PortName { get; set; } = "COM8";
        public int BaudRate { get; set; } = 115200;
        public int DataBits { get; set; } = 8;
        public string Parity { get; set; } = "None";
        public string StopBits { get; set; } = "One";
        public int ReadTimeoutMs { get; set; } = 1000;
        public int WriteTimeoutMs { get; set; } = 1000;
        public bool EnableAutoReconnect { get; set; } = true;
        public int ReconnectDelayMs { get; set; } = 5000;
    }

    public class LoggingConfig
    {
        public string LogLevel { get; set; } = "Information";
        public bool EnableFileLogging { get; set; } = true;
        public string LogFilePath { get; set; } = "logs/unimixer-.log";
        public int MaxLogFileSizeMB { get; set; } = 10;
        public int MaxLogFiles { get; set; } = 5;
        public bool EnableConsoleLogging { get; set; } = true;
    }
}