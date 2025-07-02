using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Services;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// JSON-based message processor with O(1) message type lookup using centralized logging
    /// </summary>
    public class JsonMessageProcessor : IMessageProcessor {
        private readonly ILogger<JsonMessageProcessor> _logger;
        private readonly ILoggingService _loggingService;
        private readonly Dictionary<string, MessageHandler> _handlers;
        private readonly JsonSerializerOptions _jsonOptions;

        public JsonMessageProcessor(ILogger<JsonMessageProcessor> logger, ILoggingService loggingService) {
            _logger = logger;
            _loggingService = loggingService;
            _handlers = new Dictionary<string, MessageHandler>(StringComparer.OrdinalIgnoreCase);
            _jsonOptions = new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }

        public void RegisterHandler(string messageType, MessageHandler handler) {
            if (string.IsNullOrWhiteSpace(messageType)) {
                throw new ArgumentException("Message type cannot be null or empty", nameof(messageType));
            }

            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }

            _handlers[messageType] = handler;
            _logger.LogDebug("Registered handler for message type: {MessageType}", messageType);
        }

        public async Task ProcessAsync(string rawData, string sourceInfo) {
            if (string.IsNullOrWhiteSpace(rawData)) {
                return;
            }

            // Log incoming data using centralized logging service
            _loggingService.LogDataFlow(DataFlowDirection.Incoming, rawData, sourceInfo);

            try {
                // Step 1: Generic JSON parsing
                var jsonDoc = JsonDocument.Parse(rawData);
                var root = jsonDoc.RootElement;

                // Step 2: Extract message type for O(1) lookup
                if (!root.TryGetProperty("messageType", out var messageTypeElement)) {
                    _loggingService.Log(LogLevel.Debug, "No messageType property found in JSON from {Source}", 
                        "Communication", sourceInfo);
                    return;
                }

                var messageType = messageTypeElement.GetString();
                if (string.IsNullOrWhiteSpace(messageType)) {
                    _loggingService.Log(LogLevel.Debug, "Empty messageType in JSON from {Source}", 
                        "Communication", sourceInfo);
                    return;
                }

                // Step 3: O(1) lookup for appropriate handler
                if (!_handlers.TryGetValue(messageType, out var handler)) {
                    _loggingService.Log(LogLevel.Debug, 
                        "No handler registered for message type '{MessageType}' from {Source}",
                        "Communication", messageType, sourceInfo);
                    return;
                }

                // Step 4: Create parsed message and invoke handler
                var parsedMessage = new ParsedMessage {
                    MessageType = messageType,
                    Data = root,
                    SourceInfo = sourceInfo
                };

                _loggingService.LogStructured(LogLevel.Debug, 
                    "Processing {MessageType} from {Source}", 
                    new { MessageType = messageType, Source = sourceInfo }, 
                    "Communication");

                await handler(parsedMessage);

                // Log successful processing
                _loggingService.LogCommunication(CommunicationType.JsonProtocol, rawData, sourceInfo, LogLevel.Debug);
            }
            catch (JsonException ex) {
                _loggingService.Log(LogLevel.Debug, "Failed to parse JSON from {Source}: {Error}", 
                    "Communication", sourceInfo, ex.Message);
            }
            catch (Exception ex) {
                _loggingService.Log(LogLevel.Error, 
                    "Error processing message from {Source}: {DataLength} chars - {Error}",
                    "Communication", sourceInfo, rawData.Length, ex.Message);
                _logger.LogError(ex, "Error processing message from {Source}", sourceInfo);
            }
        }

        public void Dispose() {
            // No resources to dispose - centralized logging service handles cleanup
        }
    }
}
