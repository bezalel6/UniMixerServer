using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Communication.BinaryProtocol;
using UniMixerServer.Models;
using UniMixerServer.Services;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// Binary protocol message processor - handles binary frame decoding then delegates JSON parsing to shared utility
    /// Clean implementation focused only on binary protocol processing
    /// </summary>
    public class BinaryMessageProcessor : IMessageProcessor {
        private readonly ILogger<BinaryMessageProcessor> _logger;
        private readonly Dictionary<MessageType, MessageHandler> _handlers;
        private readonly BinaryProtocolFramer _framer;
        private readonly ProtocolStatistics _statistics;

        public BinaryMessageProcessor(ILogger<BinaryMessageProcessor> logger) {
            _logger = logger;
            _handlers = new Dictionary<MessageType, MessageHandler>();
            _statistics = new ProtocolStatistics();
            _framer = new BinaryProtocolFramer(_logger, _statistics);
        }

        public void RegisterHandler(MessageType messageType, MessageHandler handler) {
            if (messageType == MessageType.INVALID) {
                throw new ArgumentException("Cannot register handler for INVALID message type", nameof(messageType));
            }
            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }

            _handlers[messageType] = handler;
            _logger.LogDebug("Registered handler for {MessageType}", messageType);
        }

        public async Task ProcessAsync(string rawData, string sourceInfo) {
            // Binary processor should only be called with binary data via ProcessBinaryAsync
            // This method exists only to satisfy the interface
            _logger.LogWarning("BinaryMessageProcessor.ProcessAsync called with string data - this should use ProcessBinaryAsync instead");
            
            // Convert string to bytes as fallback (assuming Latin1 encoding preserves byte values)
            var binaryData = System.Text.Encoding.Latin1.GetBytes(rawData);
            await ProcessBinaryAsync(binaryData, sourceInfo);
        }

        /// <summary>
        /// Process binary data: decode frames, then parse JSON using shared utility
        /// Clean binary protocol processing only - no exception detection
        /// </summary>
        public async Task ProcessBinaryAsync(byte[] binaryData, string sourceInfo) {
            if (binaryData == null || binaryData.Length == 0) {
                return;
            }

            _logger.LogTrace("🔍 Processing {Length} bytes from {Source}", binaryData.Length, sourceInfo);

            try {
                // Decode binary frames to JSON messages
                var decodedMessages = _framer.ProcessIncomingBytes(binaryData);
                
                _logger.LogDebug("Decoded {Count} messages from {Length} bytes via binary protocol", 
                    decodedMessages.Count, binaryData.Length);

                // Process each JSON message using shared parser
                foreach (var jsonMessage in decodedMessages) {
                    // Log the decoded message
                    IncomingDataLogger.LogIncomingData(jsonMessage, sourceInfo);
                    
                    // Use shared parser to handle JSON parsing and dispatch
                    await JsonMessageParser.ParseAndDispatchAsync(jsonMessage, sourceInfo, _handlers, _logger);
                }

                // Check for protocol-level issues that might indicate instability
                if (_statistics.CrcErrors > 0 || _statistics.FramingErrors > 0 || _statistics.TimeoutErrors > 0) {
                    var errorRate = (double)(_statistics.CrcErrors + _statistics.FramingErrors + _statistics.TimeoutErrors) / 
                                   Math.Max(1, _statistics.MessagesReceived + _statistics.CrcErrors + _statistics.FramingErrors);
                    
                    if (errorRate > 0.5) { // More than 50% error rate
                        _logger.LogWarning("High binary protocol error rate ({ErrorRate:P}) detected from {Source} - possible instability", 
                            errorRate, sourceInfo);
                    }
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing binary data from {Source}: {Length} bytes", sourceInfo, binaryData.Length);
            }
        }

        /// <summary>
        /// Encode a JSON message into a binary frame
        /// </summary>
        public byte[] EncodeMessage(string jsonMessage) {
            try {
                return _framer.EncodeMessage(jsonMessage);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to encode JSON message to binary frame: {Length} chars", jsonMessage.Length);
                throw;
            }
        }

        /// <summary>
        /// Get current protocol statistics for monitoring
        /// </summary>
        public ProtocolStatistics Statistics => _statistics;

        /// <summary>
        /// Get current framer state for debugging
        /// </summary>
        public ReceiveState CurrentState => _framer.CurrentState;

        public void Dispose() {
            // No resources to dispose
        }
    }
}
