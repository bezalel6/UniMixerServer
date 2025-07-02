using Serilog;
using Serilog.Core;
using System;
using System.IO;
using UniMixerServer.Configuration;

namespace UniMixerServer.Services {
    /// <summary>
    /// Service for logging all outgoing data to a dedicated log file
    /// Maintains a "latest.log" file for easy access and handles archiving
    /// </summary>
    public static class OutgoingDataLogger {
        private static Logger? _logger;
        private static Logger? _latestLogger;
        private static bool _isEnabled = false;
        private static string _logDirectory = "logs/outgoing";

        /// <summary>
        /// Initialize the outgoing data logger with configuration
        /// </summary>
        /// <param name="config">Logging configuration</param>
        public static void Initialize(LoggingConfig config) {
            Dispose(); // Clean up any existing logger

            if (config.EnableOutgoingDataLogging) {
                // Ensure directory exists
                Directory.CreateDirectory(_logDirectory);

                // Configure the main archival logger (timestamped files)
                _logger = new LoggerConfiguration()
                    .WriteTo.File(
                        Path.Combine(_logDirectory, "outgoing-data-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.MaxDataLogFiles,
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Destination}] {OutgoingData}{NewLine}")
                    .CreateLogger();

                // Configure the "latest" logger (always current, no timestamps)
                _latestLogger = new LoggerConfiguration()
                    .WriteTo.File(
                        Path.Combine(_logDirectory, "latest.log"),
                        rollingInterval: RollingInterval.Infinite, // Never roll, we manage this manually
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 1, // Only keep the latest
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Destination}] {OutgoingData}{NewLine}")
                    .CreateLogger();

                _isEnabled = true;
            }
            else {
                _isEnabled = false;
            }
        }

        /// <summary>
        /// Log outgoing data with destination information
        /// </summary>
        /// <param name="data">The data being sent</param>
        /// <param name="destination">The destination (e.g., "Serial", "MQTT:topic")</param>
        public static void LogOutgoingData(string data, string destination) {
            if (!_isEnabled || string.IsNullOrWhiteSpace(data)) {
                return;
            }

            // Log to both the archival and latest logs
            _logger?.ForContext("Destination", destination, destructureObjects: false)
                   .ForContext("OutgoingData", data, destructureObjects: false)
                   .Information("");

            _latestLogger?.ForContext("Destination", destination, destructureObjects: false)
                         .ForContext("OutgoingData", data, destructureObjects: false)
                         .Information("");
        }

        /// <summary>
        /// Dispose the logger resources
        /// </summary>
        public static void Dispose() {
            _logger?.Dispose();
            _latestLogger?.Dispose();
            _logger = null;
            _latestLogger = null;
            _isEnabled = false;
        }
    }
}
