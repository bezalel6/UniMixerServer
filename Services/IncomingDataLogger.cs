using Serilog;
using Serilog.Core;
using System;
using System.IO;
using UniMixerServer.Configuration;

namespace UniMixerServer.Services {
    /// <summary>
    /// Service for logging all incoming data to a dedicated log file
    /// Maintains a "latest.log" file for easy access and handles archiving
    /// </summary>
    public static class IncomingDataLogger {
        private static Logger? _logger;
        private static Logger? _latestLogger;
        private static bool _isEnabled = false;
        private static string _logDirectory = "logs/incoming";

        /// <summary>
        /// Initialize the incoming data logger with configuration
        /// </summary>
        /// <param name="config">Logging configuration</param>
        public static void Initialize(LoggingConfig config) {
            Dispose(); // Clean up any existing logger

            if (config.EnableIncomingDataLogging) {
                // Ensure directory exists
                Directory.CreateDirectory(_logDirectory);

                // Configure the main archival logger (timestamped files)
                _logger = new LoggerConfiguration()
                    .WriteTo.File(
                        Path.Combine(_logDirectory, "incoming-data-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.MaxDataLogFiles,
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Source}] {Message}{NewLine}")
                    .CreateLogger();

                // Configure the "latest" logger (always current, no timestamps)
                _latestLogger = new LoggerConfiguration()
                    .WriteTo.File(
                        Path.Combine(_logDirectory, "latest.log"),
                        rollingInterval: RollingInterval.Infinite, // Never roll, we manage this manually
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 1, // Only keep the latest
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Source}] {Message}{NewLine}")
                    .CreateLogger();

                _isEnabled = true;
            }
            else {
                _isEnabled = false;
            }
        }

        /// <summary>
        /// Log incoming data with source information
        /// </summary>
        /// <param name="data">The incoming data</param>
        /// <param name="source">The source (e.g., "Serial", "MQTT:topic")</param>
        public static void LogIncomingData(string data, string source) {
            if (!_isEnabled || string.IsNullOrWhiteSpace(data)) {
                return;
            }

            // Log to both the archival and latest logs
            _logger?.ForContext("Source", source).Information(data);
            _latestLogger?.ForContext("Source", source).Information(data);
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
