using Serilog;
using Serilog.Core;
using System;
using System.Text;
using UniMixerServer.Configuration;

namespace UniMixerServer.Services
{
    /// <summary>
    /// Service for logging raw binary data as ASCII to a dedicated log file
    /// This is useful for debugging protocol issues and seeing the raw data stream
    /// </summary>
    public static class BinaryDataLogger
    {
        private static Logger? _logger;
        private static bool _isEnabled = false;

        /// <summary>
        /// Initialize the binary data logger with configuration
        /// This logger is enabled when incoming data logging is enabled
        /// </summary>
        /// <param name="config">Logging configuration</param>
        public static void Initialize(LoggingConfig config)
        {
            Dispose(); // Clean up any existing logger

            if (config.EnableIncomingDataLogging)
            {
                _logger = new LoggerConfiguration()
                    .WriteTo.File(
                        "logs/incoming/binary-data-.log",
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.MaxDataLogFiles,
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        outputTemplate: "{Message}")
                    .CreateLogger();
                _isEnabled = true;
            }
            else
            {
                _isEnabled = false;
            }
        }

        /// <summary>
        /// Log raw binary data as ASCII representation
        /// </summary>
        /// <param name="binaryData">Raw binary data</param>
        /// <param name="source">The source (e.g., "Serial", "TCP")</param>
        public static void LogBinaryData(byte[] binaryData, string source)
        {
            if (!_isEnabled || _logger == null || binaryData == null || binaryData.Length == 0)
            {
                return;
            }

            // Convert binary data to ASCII representation for debugging
            var asciiData = Encoding.ASCII.GetString(binaryData);
            _logger.Information("[{Source}] {AsciiData}", source, asciiData);
        }

        /// <summary>
        /// Log a session header with timestamp and source info
        /// </summary>
        /// <param name="source">The source information</param>
        public static void LogSessionStart(string source)
        {
            if (!_isEnabled || _logger == null)
            {
                return;
            }

            _logger.Information("=== NEW SESSION STARTED: {Timestamp} - {Source} ===",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), source);
        }

        /// <summary>
        /// Dispose the logger resources
        /// </summary>
        public static void Dispose()
        {
            _logger?.Dispose();
            _logger = null;
            _isEnabled = false;
        }
    }
}
