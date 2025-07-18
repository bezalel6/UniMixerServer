using System;
using System.Collections.Generic;

namespace UniMixerServer.Configuration {
    public class AppConfig {
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
        public List<string> AllowedProcesses { get; set; } = new List<string> { "chrome", "Legcord", "Youtube Music", "Jellyfin", "cod", "hitman", "Kaizen" };
    }

    public class AudioConfig {
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
        public bool EnableDetailedLogging { get; set; } = false;
    }

    public class MqttConfig {
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

    public class MqttTopics {
        public string StatusTopic { get; set; } = "homeassistant/unimix/audio_status";
        public string CommandTopic { get; set; } = "homeassistant/unimix/audio/requests";
        public string ResponseTopic { get; set; } = "homeassistant/unimix/audio/responses";
        public string ControlTopic { get; set; } = "homeassistant/unimix/audio/control";
        public string DiscoveryPrefix { get; set; } = "homeassistant";
    }

    public class SerialConfig {
        public string PortName { get; set; } = "COM8";
        public int BaudRate { get; set; } = 115200;
        public int DataBits { get; set; } = 8;
        public string Parity { get; set; } = "None";
        public string StopBits { get; set; } = "One";
        public int ReadTimeoutMs { get; set; } = 1000;
        public int WriteTimeoutMs { get; set; } = 1000;
        public bool EnableAutoReconnect { get; set; } = true;
        public int ReconnectDelayMs { get; set; } = 5000;
        public BinaryProtocolConfig BinaryProtocol { get; set; } = new BinaryProtocolConfig();
    }

    public class BinaryProtocolConfig {
        /// <summary>
        /// Enable binary framed protocol instead of text-based JSON
        /// </summary>
        public bool EnableBinaryProtocol { get; set; } = true;

        /// <summary>
        /// Enable automatic protocol detection (fallback to text if binary fails)
        /// </summary>
        public bool EnableProtocolAutoDetection { get; set; } = true;

        /// <summary>
        /// Maximum payload size in bytes (ESP32 limit)
        /// </summary>
        public int MaxPayloadSize { get; set; } = 4096;

        /// <summary>
        /// Message timeout in milliseconds for incomplete frames
        /// </summary>
        public int MessageTimeoutMs { get; set; } = 1000;

        /// <summary>
        /// Enable detailed binary protocol logging for debugging
        /// </summary>
        public bool EnableDetailedLogging { get; set; } = false;

        /// <summary>
        /// Enable protocol statistics collection and logging
        /// </summary>
        public bool EnableStatistics { get; set; } = true;

        /// <summary>
        /// Statistics logging interval in milliseconds (0 = disabled)
        /// </summary>
        public int StatisticsLogIntervalMs { get; set; } = 60000; // Log stats every minute
    }

    /// <summary>
    /// Comprehensive logging configuration with category-specific settings
    /// </summary>
    public class LoggingConfig {
        public string LogLevel { get; set; } = "Information";
        public bool EnableFileLogging { get; set; } = true;
        public string LogFilePath { get; set; } = "logs/unimixer-.log";
        public int MaxLogFileSizeMB { get; set; } = 10;
        public int MaxLogFiles { get; set; } = 5;
        public bool EnableConsoleLogging { get; set; } = true;

        /// <summary>
        /// Whether to enable logging of incoming communication data
        /// </summary>
        public bool EnableIncomingDataLogging { get; set; } = true;

        /// <summary>
        /// Whether to enable logging of outgoing communication data
        /// </summary>
        public bool EnableOutgoingDataLogging { get; set; } = true;

        /// <summary>
        /// Path for incoming data log files
        /// </summary>
        public string IncomingDataLogPath { get; set; } = "logs/incoming/incoming-data-.log";

        /// <summary>
        /// Path for outgoing data log files
        /// </summary>
        public string OutgoingDataLogPath { get; set; } = "logs/outgoing/outgoing-data-.log";

        /// <summary>
        /// Maximum size for incoming/outgoing data log files in MB
        /// </summary>
        public int MaxDataLogFileSizeMB { get; set; } = 50;

        /// <summary>
        /// Number of data log files to retain
        /// </summary>
        public int MaxDataLogFiles { get; set; } = 30;

        /// <summary>
        /// Category-specific log level configuration
        /// </summary>
        public LoggingCategories Categories { get; set; } = new LoggingCategories();

        /// <summary>
        /// Debug and diagnostics configuration
        /// </summary>
        public LoggingDebug Debug { get; set; } = new LoggingDebug();

        /// <summary>
        /// Communication-specific logging configuration
        /// </summary>
        public LoggingCommunication Communication { get; set; } = new LoggingCommunication();
    }

    /// <summary>
    /// Category-specific logging levels
    /// </summary>
    public class LoggingCategories {
        public string AudioManager { get; set; } = "Information";
        public string Communication { get; set; } = "Information";
        public string IncomingData { get; set; } = "Debug";
        public string OutgoingData { get; set; } = "Debug";
        public string Protocol { get; set; } = "Debug";
        public string StatusUpdates { get; set; } = "Information";
        public string Performance { get; set; } = "Information";
    }

    /// <summary>
    /// Debug and diagnostics configuration
    /// </summary>
    public class LoggingDebug {
        /// <summary>
        /// Enable periodic logging of statistics
        /// </summary>
        public bool EnableStatisticsLogging { get; set; } = true;

        /// <summary>
        /// Interval in milliseconds for statistics logging (0 = disabled)
        /// </summary>
        public int StatisticsIntervalMs { get; set; } = 60000;

        /// <summary>
        /// Enable performance monitoring and logging
        /// </summary>
        public bool EnablePerformanceLogging { get; set; } = true;

        /// <summary>
        /// Enable verbose mode with detailed information
        /// </summary>
        public bool EnableVerboseMode { get; set; } = false;
    }

    /// <summary>
    /// Communication-specific logging configuration
    /// </summary>
    public class LoggingCommunication {
        /// <summary>
        /// Enable logging of data flow (incoming/outgoing messages)
        /// </summary>
        public bool EnableDataFlowLogging { get; set; } = true;

        /// <summary>
        /// Show raw data instead of formatted data
        /// </summary>
        public bool ShowRawData { get; set; } = false;

        /// <summary>
        /// Show formatted JSON data when possible
        /// </summary>
        public bool ShowFormattedData { get; set; } = true;

        /// <summary>
        /// Maximum length of data to log (longer data will be truncated)
        /// </summary>
        public int MaxDataLength { get; set; } = 1000;

        /// <summary>
        /// Enable protocol-specific logging
        /// </summary>
        public bool EnableProtocolLogging { get; set; } = true;

        /// <summary>
        /// List of field names to sanitize in logged data (passwords, tokens, etc.)
        /// </summary>
        public List<string> SensitiveDataFields { get; set; } = new List<string> {
            "password", "token", "apiKey", "secret", "auth", "credential"
        };
    }
}
