using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using UniMixerServer.Models;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// JSON-based message processor with O(1) message type lookup
    /// </summary>
    public class JsonMessageProcessor : IMessageProcessor {
        private readonly ILogger<JsonMessageProcessor> _logger;
        private readonly Dictionary<MessageType, MessageHandler> _handlers;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Logger _incomingDataLogger;

        public JsonMessageProcessor(ILogger<JsonMessageProcessor> logger) {
            _logger = logger;
            _handlers = new Dictionary<MessageType, MessageHandler>();
            _jsonOptions = new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            // Create dedicated logger for incoming data
            _incomingDataLogger = new LoggerConfiguration()
                .WriteTo.File(
                    "logs/incoming/incoming-data-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024, // 50MB
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Source}] {RawData}{NewLine}")
                .CreateLogger();
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

            // Log all incoming raw data to dedicated log file
            _incomingDataLogger.Information("Incoming data from {Source}: {RawData}", sourceInfo, rawData);

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

                // Handle both numeric and string message types for backward compatibility during transition
                if (messageTypeElement.ValueKind == JsonValueKind.Number) {
                    // New numeric protocol
                    var messageTypeValue = messageTypeElement.GetInt32();
                    if (Enum.IsDefined(typeof(MessageType), messageTypeValue)) {
                        messageType = (MessageType)messageTypeValue;
                    }
                    else {
                        _logger.LogDebug("Unknown numeric messageType {MessageTypeValue} from {Source}", messageTypeValue, sourceInfo);
                        messageType = MessageType.INVALID;
                    }
                }
                else if (messageTypeElement.ValueKind == JsonValueKind.String) {
                    // Legacy string protocol - convert to enum
                    var messageTypeString = messageTypeElement.GetString();
                    messageType = MessageTypeExtensions.FromMessageString(messageTypeString ?? "");
                    _logger.LogDebug("Converting legacy string messageType '{MessageTypeString}' to enum {MessageType} from {Source}",
                        messageTypeString, messageType, sourceInfo);
                }
                else {
                    _logger.LogDebug("Invalid messageType format in JSON from {Source}", sourceInfo);
                    return;
                }

                if (messageType == MessageType.INVALID) {
                    _logger.LogDebug("Invalid or unknown messageType from {Source}", sourceInfo);
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
            _incomingDataLogger?.Dispose();
        }
    }
}
