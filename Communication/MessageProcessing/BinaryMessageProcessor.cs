using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Communication.BinaryProtocol;
using UniMixerServer.Services;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// Binary protocol message processor - handles binary frame decoding then delegates JSON parsing to shared utility
    /// Clean implementation focused only on binary protocol processing
    /// </summary>
    public class BinaryMessageProcessor : IMessageProcessor {
        private readonly ILogger<BinaryMessageProcessor> _logger;
        private readonly Dictionary<string, MessageHandler> _handlers;
        private readonly BinaryProtocolFramer _framer;
        private readonly ProtocolStatistics _statistics;

        public BinaryMessageProcessor(ILogger<BinaryMessageProcessor> logger) {
            _logger = logger;
            _handlers = new Dictionary<string, MessageHandler>();
            _statistics = new ProtocolStatistics();
            _framer = new BinaryProtocolFramer(_logger, _statistics);
        }

        public void RegisterHandler(string messageType, MessageHandler handler) {
            if (string.IsNullOrEmpty(messageType)) {
                throw new ArgumentException("Cannot register handler for empty message type", nameof(messageType));
            }
            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }

            _handlers[messageType] = handler;
            _logger.LogDebug("Registered handler for {MessageType}", messageType);
        }

        public async Task ProcessAsync(string rawData, string sourceInfo) {
            if (string.IsNullOrWhiteSpace(rawData)) {
                return;
            }

            // Log incoming data
            IncomingDataLogger.LogIncomingData(rawData, sourceInfo);

            // Use shared parser to handle all JSON parsing logic (eliminates duplication)
            await JsonMessageParser.ParseAndDispatchAsync(rawData, sourceInfo, _handlers, _logger);
        }

        public async Task ProcessBinaryAsync(byte[] binaryData, string sourceInfo) {
            if (binaryData == null || binaryData.Length == 0) {
                return;
            }

            // Log binary data for debugging
            BinaryDataLogger.LogBinaryData(binaryData, sourceInfo);

            // Decode binary frames
            var messages = _framer.ProcessIncomingBytes(binaryData);
            foreach (var message in messages) {
                if (!string.IsNullOrEmpty(message)) {
                    // Log decoded message
                    IncomingDataLogger.LogIncomingData(message, sourceInfo);

                    // Use shared parser to handle JSON parsing
                    await JsonMessageParser.ParseAndDispatchAsync(message, sourceInfo, _handlers, _logger);
                }
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

        public ProtocolStatistics Statistics => _statistics;

        public void Dispose() {
            // No resources to dispose
        }
    }
}
