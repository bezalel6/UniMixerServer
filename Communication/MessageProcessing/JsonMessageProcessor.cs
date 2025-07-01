using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Models;
using UniMixerServer.Services;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// JSON-based message processor with O(1) message type lookup
    /// </summary>
    public class JsonMessageProcessor : IMessageProcessor {
        private readonly ILogger<JsonMessageProcessor> _logger;
        private readonly Dictionary<MessageType, MessageHandler> _handlers;
        private readonly JsonSerializerOptions _jsonOptions;

        public JsonMessageProcessor(ILogger<JsonMessageProcessor> logger) {
            _logger = logger;
            _handlers = new Dictionary<MessageType, MessageHandler>();
            _jsonOptions = new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }

        public void RegisterHandler(MessageType messageType, MessageHandler handler) {
            if (messageType == MessageType.INVALID) {
                throw new ArgumentException("Cannot register handler for INVALID message type", nameof(messageType));
            }

            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }

            _handlers[messageType] = handler;
            _logger.LogDebug("Registered handler for message type: {MessageType} ({MessageTypeValue})", messageType, (int)messageType);
        }

        public async Task ProcessAsync(string rawData, string sourceInfo) {
            if (string.IsNullOrWhiteSpace(rawData)) {
                return;
            }

            // Log all incoming raw data using the configurable logger
            IncomingDataLogger.LogIncomingData(rawData, sourceInfo);

            try {
                // Step 1: Generic JSON parsing
                var jsonDoc = JsonDocument.Parse(rawData);
                var root = jsonDoc.RootElement;

                // Step 2: Extract message type for O(1) lookup
                if (!root.TryGetProperty("messageType", out var messageTypeElement)) {
                    _logger.LogDebug("No messageType property found in JSON from {Source}", sourceInfo);
                    return;
                }

                MessageType messageType;
                if (messageTypeElement.ValueKind == JsonValueKind.String) {
                    if (!Enum.TryParse<MessageType>(messageTypeElement.GetString(), true, out messageType)) {
                        _logger.LogDebug("Invalid message type string '{MessageTypeString}' from {Source}",
                            messageTypeElement.GetString(), sourceInfo);
                        return;
                    }
                }
                else if (messageTypeElement.ValueKind == JsonValueKind.Number) {
                    var messageTypeValue = messageTypeElement.GetInt32();
                    if (!Enum.IsDefined(typeof(MessageType), messageTypeValue)) {
                        _logger.LogDebug("Invalid message type number {MessageTypeNumber} from {Source}",
                            messageTypeValue, sourceInfo);
                        return;
                    }
                    messageType = (MessageType)messageTypeValue;
                }
                else {
                    _logger.LogDebug("Invalid messageType property type from {Source}", sourceInfo);
                    return;
                }

                // Step 3: O(1) lookup for appropriate handler
                if (!_handlers.TryGetValue(messageType, out var handler)) {
                    _logger.LogDebug("No handler registered for message type '{MessageType}' ({MessageTypeValue}) from {Source}",
                        messageType, (int)messageType, sourceInfo);
                    return;
                }

                // Step 4: Create parsed message and invoke handler
                var parsedMessage = new ParsedMessage {
                    MessageType = messageType,
                    Data = root,
                    SourceInfo = sourceInfo
                };

                _logger.LogTrace("Processing {MessageType} ({MessageTypeValue}) from {Source}", messageType, (int)messageType, sourceInfo);
                await handler(parsedMessage);
            }
            catch (JsonException ex) {
                _logger.LogDebug("Failed to parse JSON from {Source}: {Error}", sourceInfo, ex.Message);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing message from {Source}: {DataLength} chars",
                    sourceInfo, rawData.Length);
            }
        }

        public void Dispose() {
            // No longer need to dispose anything as we're using the static IncomingDataLogger
        }
    }
}
