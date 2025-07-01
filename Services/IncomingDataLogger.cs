using Serilog;
using Serilog.Core;
using System;
using UniMixerServer.Configuration;

namespace UniMixerServer.Services
{
    /// <summary>
    /// Service for logging all incoming data to a dedicated log file
    /// </summary>
    public static class IncomingDataLogger
    {
        private static Logger? _logger;
        private static bool _isEnabled = false;

        /// <summary>
        /// Initialize the incoming data logger with configuration
        /// </summary>
        /// <param name="config">Logging configuration</param>
        public static void Initialize(LoggingConfig config)
        {
            Dispose(); // Clean up any existing logger

            if (config.EnableIncomingDataLogging)
            {
                _logger = new LoggerConfiguration()
                    .WriteTo.File(
                        config.IncomingDataLogPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.MaxDataLogFiles,
                        fileSizeLimitBytes: config.MaxDataLogFileSizeMB * 1024 * 1024,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Source}] {RawData}{NewLine}")
                    .CreateLogger();
                _isEnabled = true;
            }
            else
            {
                _isEnabled = false;
            }
        }

        /// <summary>
        /// Log incoming data with source information
        /// </summary>
        /// <param name="data">The incoming data</param>
        /// <param name="source">The source (e.g., "Serial", "MQTT:topic")</param>
        public static void LogIncomingData(string data, string source)
        {
            if (!_isEnabled || _logger == null || string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            _logger.Information("Incoming data from {Source}: {RawData}", source, data);
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
