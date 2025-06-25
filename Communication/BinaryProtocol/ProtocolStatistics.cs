using System;
using System.Threading;

namespace UniMixerServer.Communication.BinaryProtocol {
    /// <summary>
    /// Tracks comprehensive communication statistics for the binary protocol
    /// </summary>
    public class ProtocolStatistics {
        private long _messagesSent;
        private long _messagesReceived;
        private long _bytesTransmitted;
        private long _bytesReceived;
        private long _framingErrors;
        private long _crcErrors;
        private long _timeoutErrors;
        private long _bufferOverflowErrors;
        private long _escapeSequenceErrors;
        private readonly object _lockObject = new object();
        private DateTime _startTime;

        public ProtocolStatistics() {
            _startTime = DateTime.UtcNow;
        }

        // Message counters
        public long MessagesSent => _messagesSent;
        public long MessagesReceived => _messagesReceived;
        public long BytesTransmitted => _bytesTransmitted;
        public long BytesReceived => _bytesReceived;

        // Error counters
        public long FramingErrors => _framingErrors;
        public long CrcErrors => _crcErrors;
        public long TimeoutErrors => _timeoutErrors;
        public long BufferOverflowErrors => _bufferOverflowErrors;
        public long EscapeSequenceErrors => _escapeSequenceErrors;

        // Calculated properties
        public long TotalErrors => _framingErrors + _crcErrors + _timeoutErrors + _bufferOverflowErrors + _escapeSequenceErrors;
        public double SuccessRate => _messagesReceived > 0 ? (double)(_messagesReceived - TotalErrors) / _messagesReceived * 100.0 : 0.0;
        public TimeSpan UpTime => DateTime.UtcNow - _startTime;
        public double MessagesPerSecond => UpTime.TotalSeconds > 0 ? (_messagesSent + _messagesReceived) / UpTime.TotalSeconds : 0.0;
        public double BytesPerSecond => UpTime.TotalSeconds > 0 ? (_bytesTransmitted + _bytesReceived) / UpTime.TotalSeconds : 0.0;

        public void IncrementMessagesSent() => Interlocked.Increment(ref _messagesSent);
        public void IncrementMessagesReceived() => Interlocked.Increment(ref _messagesReceived);
        public void AddBytesTransmitted(long bytes) => Interlocked.Add(ref _bytesTransmitted, bytes);
        public void AddBytesReceived(long bytes) => Interlocked.Add(ref _bytesReceived, bytes);
        public void IncrementFramingErrors() => Interlocked.Increment(ref _framingErrors);
        public void IncrementCrcErrors() => Interlocked.Increment(ref _crcErrors);
        public void IncrementTimeoutErrors() => Interlocked.Increment(ref _timeoutErrors);
        public void IncrementBufferOverflowErrors() => Interlocked.Increment(ref _bufferOverflowErrors);
        public void IncrementEscapeSequenceErrors() => Interlocked.Increment(ref _escapeSequenceErrors);

        public void Reset() {
            lock (_lockObject) {
                _messagesSent = 0;
                _messagesReceived = 0;
                _bytesTransmitted = 0;
                _bytesReceived = 0;
                _framingErrors = 0;
                _crcErrors = 0;
                _timeoutErrors = 0;
                _bufferOverflowErrors = 0;
                _escapeSequenceErrors = 0;
                _startTime = DateTime.UtcNow;
            }
        }

        public string GetSummary() {
            return $"Messages: Sent={MessagesSent}, Received={MessagesReceived}, " +
                   $"Bytes: TX={BytesTransmitted}, RX={BytesReceived}, " +
                   $"Errors: Framing={FramingErrors}, CRC={CrcErrors}, Timeout={TimeoutErrors}, " +
                   $"Overflow={BufferOverflowErrors}, Escape={EscapeSequenceErrors}, " +
                   $"Success Rate={SuccessRate:F2}%, " +
                   $"Performance: {MessagesPerSecond:F2} msg/s, {BytesPerSecond:F2} B/s, " +
                   $"Uptime: {UpTime:hh\\:mm\\:ss}";
        }
    }
}
