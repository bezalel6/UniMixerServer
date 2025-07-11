using Serilog;
using Serilog.Core;
using System;
using System.IO;
using System.Text;
using UniMixerServer.Configuration;

namespace UniMixerServer.Services {
    /// <summary>
    /// Service for logging raw binary data as ASCII to a dedicated log file
    /// This is useful for debugging protocol issues and seeing the raw data stream
    /// Maintains a "latest.log" file for easy access and handles archiving
    /// </summary>
    public static class BinaryDataLogger {
        private static Logger? _logger;
        private static Logger? _latestLogger;
        private static bool _isEnabled = false;
        private static string _logDirectory = "logs/binary";

        /// <summary>
        /// Initialize the binary data logger with configuration
        /// This logger is enabled when incoming data logging is enabled
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
                        Path.Combine(_logDirectory, "binary-data-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.MaxDataLogFiles,
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        outputTemplate: "{Message}")
                    .CreateLogger();

                // Configure the "latest" logger (always current, no timestamps)
                _latestLogger = new LoggerConfiguration()
                    .WriteTo.File(
                        Path.Combine(_logDirectory, "latest.log"),
                        rollingInterval: RollingInterval.Infinite, // Never roll, we manage this manually
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 1, // Only keep the latest
                        outputTemplate: "{Message}")
                    .CreateLogger();

                _isEnabled = true;
            }
            else {
                _isEnabled = false;
            }
        }

        /// <summary>
        /// Log raw binary data as ASCII representation
        /// </summary>
        /// <param name="binaryData">Raw binary data</param>
        /// <param name="source">The source (e.g., "Serial", "TCP")</param>
        public static void LogBinaryData(byte[] binaryData, string source) {
            if (!_isEnabled || binaryData == null || binaryData.Length == 0) {
                return;
            }

            // Convert binary data to ASCII representation for debugging
            var asciiData = Encoding.UTF8.GetString(binaryData);

            // Log to both archival and latest logs
            _logger?.Information(asciiData);
            _latestLogger?.Information(asciiData);
        }

        /// <summary>
        /// Log a session header with timestamp and source info
        /// </summary>
        /// <param name="source">The source information</param>
        public static void LogSessionStart(string source) {
            if (!_isEnabled) {
                return;
            }

            var sessionHeader = $"=== NEW SESSION STARTED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {source} ===";

            // Log to both archival and latest logs
            _logger?.Information(sessionHeader);
            _latestLogger?.Information(sessionHeader);
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

        /// <summary>
        /// Clear the latest.log file by disposing and recreating the latest logger
        /// </summary>
        public static void ClearLatestLog() {
            if (!_isEnabled) return;

            try {
                // Dispose the latest logger to flush any pending writes
                _latestLogger?.Dispose();
                
                // Clear the file
                var latestLogPath = Path.Combine(_logDirectory, "latest.log");
                if (File.Exists(latestLogPath)) {
                    File.WriteAllText(latestLogPath, string.Empty);
                }

                // Recreate the latest logger
                _latestLogger = new LoggerConfiguration()
                    .WriteTo.File(
                        latestLogPath,
                        rollingInterval: RollingInterval.Infinite,
                        fileSizeLimitBytes: 50 * 1024 * 1024, // Default 50MB
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 1,
                        outputTemplate: "{Message}")
                    .CreateLogger();
            }
            catch (Exception) {
                // If clearing fails, just recreate the logger
                _latestLogger = new LoggerConfiguration()
                    .WriteTo.File(
                        Path.Combine(_logDirectory, "latest.log"),
                        rollingInterval: RollingInterval.Infinite,
                        fileSizeLimitBytes: 50 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 1,
                        outputTemplate: "{Message}")
                    .CreateLogger();
            }
        }
    }
}
