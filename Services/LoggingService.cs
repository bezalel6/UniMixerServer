using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using UniMixerServer.Configuration;

namespace UniMixerServer.Services {
    public class LoggingService : ILoggingService, IDisposable {
        private readonly ILogger<LoggingService> _baseLogger;
        private readonly LoggingConfig _config;
        private readonly Dictionary<string, LogLevel> _categoryLevels;
        private readonly LoggingStatistics _statistics;
        private readonly Timer _statisticsTimer;
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        public LoggingService(ILogger<LoggingService> baseLogger, LoggingConfig config) {
            _baseLogger = baseLogger;
            _config = config;
            _statistics = new LoggingStatistics();

            // Initialize category log levels
            _categoryLevels = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase) {
                ["AudioManager"] = ParseLogLevel(_config.Categories.AudioManager),
                ["Communication"] = ParseLogLevel(_config.Categories.Communication),
                ["IncomingData"] = ParseLogLevel(_config.Categories.IncomingData),
                ["OutgoingData"] = ParseLogLevel(_config.Categories.OutgoingData),
                ["Protocol"] = ParseLogLevel(_config.Categories.Protocol),
                ["StatusUpdates"] = ParseLogLevel(_config.Categories.StatusUpdates),
                ["Performance"] = ParseLogLevel(_config.Categories.Performance)
            };

            // Start statistics timer if enabled
            if (_config.Debug.EnableStatisticsLogging && _config.Debug.StatisticsIntervalMs > 0) {
                _statisticsTimer = new Timer(LogStatistics, null,
                    TimeSpan.FromMilliseconds(_config.Debug.StatisticsIntervalMs),
                    TimeSpan.FromMilliseconds(_config.Debug.StatisticsIntervalMs));
            }

            Log(LogLevel.Information, "Centralized logging service initialized", "System");
        }

        public void LogDataFlow(DataFlowDirection direction, string data, string source, string destination = null) {
            if (!_config.Communication.EnableDataFlowLogging) return;
            if (string.IsNullOrWhiteSpace(data)) return;

            var category = direction == DataFlowDirection.Incoming ? "IncomingData" : "OutgoingData";
            var effectiveLevel = GetEffectiveLogLevel(category);

            if (!ShouldLog(effectiveLevel, category)) return;

            var sanitizedData = SanitizeData(data);
            var displayData = _config.Communication.ShowRawData ? sanitizedData : FormatData(sanitizedData);

            if (displayData.Length > _config.Communication.MaxDataLength) {
                displayData = displayData.Substring(0, _config.Communication.MaxDataLength) + "... (truncated)";
            }

            using var scope = BeginScope(new Dictionary<string, object> {
                ["Direction"] = direction.ToString(),
                ["Source"] = source,
                ["Destination"] = destination ?? "Unknown",
                ["DataLength"] = data.Length,
                ["Category"] = category
            });

            var message = destination != null
                ? "[{Direction}] {Source} → {Destination}: {Data}"
                : "[{Direction}] {Source}: {Data}";

            LogInternal(effectiveLevel, message, category, direction, source, destination ?? "", displayData);

            _statistics.IncrementDataFlow(direction, data.Length);
        }

        public void LogCommunication(CommunicationType type, string data, string source, LogLevel level = LogLevel.Information) {
            var direction = type.ToString().Contains("Incoming") ? DataFlowDirection.Incoming : DataFlowDirection.Outgoing;
            var destination = ExtractDestinationFromType(type);

            // Log data flow with detailed information
            LogDataFlow(direction, data, source, destination);

            // Log communication event with type-specific information
            using var scope = BeginScope(new Dictionary<string, object> {
                ["CommunicationType"] = type.ToString(),
                ["Source"] = source,
                ["Category"] = "Communication"
            });

            var messageType = ExtractMessageType(data);
            LogInternal(level, "[{CommunicationType}] {Source}: {MessageType} message processed",
                "Communication", type, source, messageType);
        }

        public void LogProtocol(string message, string protocol, LogLevel level = LogLevel.Debug) {
            if (!_config.Communication.EnableProtocolLogging) return;

            using var scope = BeginScope(new Dictionary<string, object> {
                ["Protocol"] = protocol,
                ["Category"] = "Protocol"
            });

            LogInternal(level, "[{Protocol}] {Message}", "Protocol", protocol, message);
        }

        public void LogPerformance(string operation, TimeSpan duration, Dictionary<string, object> metadata = null) {
            if (!_config.Debug.EnablePerformanceLogging) return;

            using var scope = BeginScope(new Dictionary<string, object> {
                ["Operation"] = operation,
                ["Duration"] = duration.TotalMilliseconds,
                ["Category"] = "Performance"
            });

            var metadataStr = metadata != null ?
                string.Join(", ", metadata.Select(kvp => $"{kvp.Key}={kvp.Value}")) : "";

            LogInternal(LogLevel.Information,
                "Performance: {Operation} completed in {Duration:F2}ms{Metadata}",
                "Performance", operation, duration.TotalMilliseconds,
                metadataStr.Length > 0 ? $" ({metadataStr})" : "");
        }

        public void LogStructured<T>(LogLevel level, string messageTemplate, T data, string category = null) {
            category ??= "General";

            if (!ShouldLog(level, category)) return;

            using var scope = BeginScope(new Dictionary<string, object> {
                ["Category"] = category,
                ["Data"] = data
            });

            LogInternal(level, messageTemplate, category, data);
        }

        public void Log(LogLevel level, string message, string category = null, params object[] args) {
            category ??= "General";

            if (!ShouldLog(level, category)) return;

            using var scope = BeginScope(new Dictionary<string, object> {
                ["Category"] = category
            });

            LogInternal(level, message, category, args);
        }

        public void UpdateLogLevel(string category, LogLevel level) {
            lock (_lockObject) {
                _categoryLevels[category] = level;
            }

            Log(LogLevel.Information, "Log level updated: {Category} → {Level}", "System", category, level);
        }

        public LogLevel GetEffectiveLogLevel(string category) {
            lock (_lockObject) {
                if (_categoryLevels.TryGetValue(category, out var level)) {
                    return level;
                }
                return ParseLogLevel(_config.LogLevel);
            }
        }

        public LoggingStatistics GetStatistics() {
            return _statistics;
        }

        public void ResetStatistics() {
            _statistics.Reset();
            Log(LogLevel.Information, "Logging statistics reset", "System");
        }

        public IDisposable BeginScope<TState>(TState state) {
            return _baseLogger.BeginScope(state);
        }

        private bool ShouldLog(LogLevel level, string category) {
            var effectiveLevel = GetEffectiveLogLevel(category);
            return level >= effectiveLevel;
        }

        private void LogInternal(LogLevel level, string message, string category, params object[] args) {
            if (!ShouldLog(level, category)) return;

            _baseLogger.Log(level, message, args);
            _statistics.IncrementCategory(category);
        }

        private string SanitizeData(string data) {
            if (string.IsNullOrEmpty(data)) return data;

            var sanitized = data;
            foreach (var sensitiveField in _config.Communication.SensitiveDataFields) {
                var pattern = $@"""{sensitiveField}"":\s*""[^""]*""";
                sanitized = Regex.Replace(sanitized, pattern, $@"""{sensitiveField}"":""***""", RegexOptions.IgnoreCase);
            }

            return sanitized;
        }

        private string FormatData(string data) {
            if (string.IsNullOrEmpty(data)) return data;
            if (!_config.Communication.ShowFormattedData) return data;

            try {
                // Try to format as JSON for better readability
                var jsonDoc = JsonDocument.Parse(data);
                return JsonSerializer.Serialize(jsonDoc, new JsonSerializerOptions {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch {
                // If not JSON, return as-is
                return data;
            }
        }

        private string ExtractMessageType(string data) {
            if (string.IsNullOrEmpty(data)) return "Unknown";

            try {
                var jsonDoc = JsonDocument.Parse(data);
                if (jsonDoc.RootElement.TryGetProperty("messageType", out var messageTypeElement)) {
                    return messageTypeElement.GetString() ?? "Unknown";
                }
                if (jsonDoc.RootElement.TryGetProperty("MessageType", out var messageTypeElement2)) {
                    return messageTypeElement2.GetString() ?? "Unknown";
                }
            }
            catch {
                // Not JSON or parsing failed
            }

            return "Unknown";
        }

        private string ExtractDestinationFromType(CommunicationType type) {
            return type switch {
                CommunicationType.SerialIncoming or CommunicationType.SerialOutgoing => "Serial",
                CommunicationType.MqttIncoming or CommunicationType.MqttOutgoing => "MQTT",
                CommunicationType.BinaryProtocol => "Binary",
                CommunicationType.JsonProtocol => "JSON",
                _ => "Internal"
            };
        }

        private LogLevel ParseLogLevel(string logLevel) {
            return logLevel?.ToUpperInvariant() switch {
                "TRACE" => LogLevel.Trace,
                "DEBUG" => LogLevel.Debug,
                "INFORMATION" or "INFO" => LogLevel.Information,
                "WARNING" or "WARN" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                "CRITICAL" or "FATAL" => LogLevel.Critical,
                _ => LogLevel.Information
            };
        }

        private void LogStatistics(object state) {
            if (_disposed) return;

            try {
                var summary = _statistics.GetSummary();
                Log(LogLevel.Information, "Logging Statistics: {Summary}", "Statistics", summary);

                if (_config.Debug.EnableVerboseMode) {
                    var categoryCounters = _statistics.GetCategoryCounters();
                    foreach (var kvp in categoryCounters) {
                        Log(LogLevel.Debug, "Category {Category}: {Count} events", "Statistics", kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex) {
                _baseLogger.LogError(ex, "Error logging statistics");
            }
        }

        public void Dispose() {
            if (_disposed) return;

            _statisticsTimer?.Dispose();

            Log(LogLevel.Information, "Centralized logging service disposing", "System");
            _disposed = true;
        }
    }
}
