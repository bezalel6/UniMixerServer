using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Models;
using UniMixerServer.Services;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// JSON message processor - simplified to use shared parsing utility
    /// </summary>
    public class JsonMessageProcessor : IMessageProcessor {
        private readonly ILogger<JsonMessageProcessor> _logger;
        private readonly Dictionary<MessageType, MessageHandler> _handlers;

        public JsonMessageProcessor(ILogger<JsonMessageProcessor> logger) {
            _logger = logger;
            _handlers = new Dictionary<MessageType, MessageHandler>();
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
            if (string.IsNullOrWhiteSpace(rawData)) {
                return;
            }

            // Log incoming data
            IncomingDataLogger.LogIncomingData(rawData, sourceInfo);

            // Use shared parser to handle all JSON parsing logic (eliminates duplication)
            await JsonMessageParser.ParseAndDispatchAsync(rawData, sourceInfo, _handlers, _logger);
        }

        public void Dispose() {
            // No resources to dispose
        }
    }
}
