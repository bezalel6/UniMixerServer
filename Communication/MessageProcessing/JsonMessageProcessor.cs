using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// JSON-based message processor with O(1) message type lookup
    /// </summary>
    public class JsonMessageProcessor : IMessageProcessor {
        private readonly ILogger<JsonMessageProcessor> _logger;
        private readonly Dictionary<string, MessageHandler> _handlers;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Logger _incomingDataLogger;

        public JsonMessageProcessor(ILogger<JsonMessageProcessor> logger) {
            _logger = logger;
            _handlers = new Dictionary<string, MessageHandler>(StringComparer.OrdinalIgnoreCase);
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

                var messageType = messageTypeElement.GetString();
                if (string.IsNullOrWhiteSpace(messageType)) {
                    _logger.LogDebug("Empty messageType in JSON from {Source}", sourceInfo);
                    return;
                }

                // Step 3: O(1) lookup for appropriate handler
                if (!_handlers.TryGetValue(messageType, out var handler)) {
                    _logger.LogDebug("No handler registered for message type '{MessageType}' from {Source}",
                        messageType, sourceInfo);
                    return;
                }

                // Step 4: Create parsed message and invoke handler
                var parsedMessage = new ParsedMessage {
                    MessageType = messageType,
                    Data = root,
                    SourceInfo = sourceInfo
                };

                _logger.LogTrace("Processing {MessageType} from {Source}", messageType, sourceInfo);
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
