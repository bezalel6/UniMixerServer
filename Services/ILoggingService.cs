using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace UniMixerServer.Services {
    public interface ILoggingService {
        // Core logging methods
        void LogCommunication(CommunicationType type, string data, string source, LogLevel level = LogLevel.Information);
        void LogDataFlow(DataFlowDirection direction, string data, string source, string destination = null);
        void LogProtocol(string message, string protocol, LogLevel level = LogLevel.Debug);
        void LogPerformance(string operation, TimeSpan duration, Dictionary<string, object> metadata = null);
        
        // Structured logging
        void LogStructured<T>(LogLevel level, string messageTemplate, T data, string category = null);
        void Log(LogLevel level, string message, string category = null, params object[] args);
        
        // Dynamic configuration
        void UpdateLogLevel(string category, LogLevel level);
        LogLevel GetEffectiveLogLevel(string category);
        
        // Statistics and monitoring
        LoggingStatistics GetStatistics();
        void ResetStatistics();
        
        // Scoped logging
        IDisposable BeginScope<TState>(TState state);
    }

    public enum CommunicationType {
        SerialIncoming,
        SerialOutgoing,
        MqttIncoming,
        MqttOutgoing,
        InternalMessage,
        StatusBroadcast,
        AssetRequest,
        AssetResponse,
        BinaryProtocol,
        JsonProtocol
    }

    public enum DataFlowDirection {
        Incoming,
        Outgoing,
        Internal,
        Broadcast
    }

    public class LoggingStatistics {
        private long _incomingMessages;
        private long _outgoingMessages;
        private long _incomingBytes;
        private long _outgoingBytes;
        private long _totalLogEvents;
        private readonly Dictionary<string, long> _categoryCounters;
        private readonly object _lock = new object();

        public LoggingStatistics() {
            _categoryCounters = new Dictionary<string, long>();
        }

        public long IncomingMessages => _incomingMessages;
        public long OutgoingMessages => _outgoingMessages;
        public long IncomingBytes => _incomingBytes;
        public long OutgoingBytes => _outgoingBytes;
        public long TotalLogEvents => _totalLogEvents;
        public DateTime StartTime { get; } = DateTime.UtcNow;

        public void IncrementDataFlow(DataFlowDirection direction, int bytes) {
            lock (_lock) {
                if (direction == DataFlowDirection.Incoming) {
                    _incomingMessages++;
                    _incomingBytes += bytes;
                }
                else if (direction == DataFlowDirection.Outgoing) {
                    _outgoingMessages++;
                    _outgoingBytes += bytes;
                }
                _totalLogEvents++;
            }
        }

        public void IncrementCategory(string category) {
            lock (_lock) {
                _categoryCounters.TryGetValue(category, out var count);
                _categoryCounters[category] = count + 1;
                _totalLogEvents++;
            }
        }

        public Dictionary<string, long> GetCategoryCounters() {
            lock (_lock) {
                return new Dictionary<string, long>(_categoryCounters);
            }
        }

        public void Reset() {
            lock (_lock) {
                _incomingMessages = 0;
                _outgoingMessages = 0;
                _incomingBytes = 0;
                _outgoingBytes = 0;
                _totalLogEvents = 0;
                _categoryCounters.Clear();
            }
        }

        public string GetSummary() {
            var uptime = DateTime.UtcNow - StartTime;
            return $"Uptime: {uptime:hh\\:mm\\:ss}, " +
                   $"Events: {_totalLogEvents}, " +
                   $"In: {_incomingMessages} msgs ({_incomingBytes:N0} bytes), " +
                   $"Out: {_outgoingMessages} msgs ({_outgoingBytes:N0} bytes)";
        }
    }
}