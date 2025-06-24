using Serilog;
using Serilog.Core;
using System;

namespace UniMixerServer.Services {
    /// <summary>
    /// Service for logging all outgoing data to a dedicated log file
    /// </summary>
    public static class OutgoingDataLogger {
        private static readonly Logger _logger;

        static OutgoingDataLogger() {
            _logger = new LoggerConfiguration()
                .WriteTo.File(
                    "logs/outgoing/outgoing-data-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024, // 50MB
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Destination}] {OutgoingData}{NewLine}")
                .CreateLogger();
        }

        /// <summary>
        /// Log outgoing data with destination information
        /// </summary>
        /// <param name="data">The data being sent</param>
        /// <param name="destination">The destination (e.g., "Serial", "MQTT:topic")</param>
        public static void LogOutgoingData(string data, string destination) {
            if (string.IsNullOrWhiteSpace(data)) {
                return;
            }

            _logger.Information("Outgoing data to {Destination}: {OutgoingData}", destination, data);
        }

        /// <summary>
        /// Dispose the logger resources
        /// </summary>
        public static void Dispose() {
            _logger?.Dispose();
        }
    }
}
