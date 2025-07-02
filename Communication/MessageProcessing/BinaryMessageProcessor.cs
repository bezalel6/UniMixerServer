using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Communication.BinaryProtocol;
using UniMixerServer.Services;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// Binary protocol message processor with O(1) message type lookup
    /// Handles binary frames but processes JSON payloads like the regular JsonMessageProcessor
    /// </summary>
    public class BinaryMessageProcessor : IMessageProcessor {
        private readonly ILogger<BinaryMessageProcessor> _logger;
        private readonly ILoggingService _loggingService;
        private readonly Dictionary<string, MessageHandler> _handlers;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly BinaryProtocolFramer _framer;
        private readonly ProtocolStatistics _statistics;
        private readonly Func<string, string, Task>? _forwardDecodedMessage;

        public BinaryMessageProcessor(ILogger<BinaryMessageProcessor> logger, ILoggingService loggingService, Func<string, string, Task>? forwardDecodedMessage = null) {
            _logger = logger;
            _loggingService = loggingService;
            _handlers = new Dictionary<string, MessageHandler>(StringComparer.OrdinalIgnoreCase);
            _jsonOptions = new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
            _forwardDecodedMessage = forwardDecodedMessage;

            // Initialize binary protocol components
            _statistics = new ProtocolStatistics();
            _framer = new BinaryProtocolFramer(_logger, _statistics);
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
            // For binary message processor, rawData should be treated as binary data
            // Convert string to bytes (this assumes the calling code passed binary data as string)
            byte[] binaryData;

            try {
                // If rawData contains binary data encoded as string, convert it back to bytes
                // In practice, the SerialHandler should call ProcessBinaryAsync directly
                binaryData = System.Text.Encoding.Latin1.GetBytes(rawData);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to convert raw data to binary from {Source}", sourceInfo);
                return;
            }

            await ProcessBinaryAsync(binaryData, sourceInfo);
        }

        /// <summary>
        /// Process binary data through the binary protocol framer
        /// </summary>
        /// <param name="binaryData">Raw binary data</param>
        /// <param name="sourceInfo">Source information for logging</param>
        /// <returns>Task representing the processing</returns>
        public async Task ProcessBinaryAsync(byte[] binaryData, string sourceInfo) {
            if (binaryData == null || binaryData.Length == 0) {
                return;
            }

            try {
                // Process binary data through the framer
                var decodedMessages = _framer.ProcessIncomingBytes(binaryData);

                // Process each decoded JSON message through the regular JSON processing pipeline
                foreach (var jsonMessage in decodedMessages) {
                    // Log decoded message using centralized logging service
                    _loggingService.LogDataFlow(DataFlowDirection.Incoming, jsonMessage, sourceInfo);
                    _loggingService.LogCommunication(CommunicationType.BinaryProtocol, jsonMessage, sourceInfo, LogLevel.Debug);

                    // Forward to existing message processor if available, otherwise process locally
                    if (_forwardDecodedMessage != null) {
                        await _forwardDecodedMessage(jsonMessage, sourceInfo);
                    }
                    else {
                        await ProcessDecodedJsonMessage(jsonMessage, sourceInfo);
                    }
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing data from {Source}: {DataLength} bytes",
                    sourceInfo, binaryData.Length);
            }
        }

        private async Task ProcessDecodedJsonMessage(string jsonMessage, string sourceInfo) {
            try {
                // Step 1: Generic JSON parsing
                var jsonDoc = JsonDocument.Parse(jsonMessage);
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
                _logger.LogError(ex, "Error processing message from {Source}: {MessageLength} chars",
                    sourceInfo, jsonMessage.Length);
            }
        }

        /// <summary>
        /// Encode a JSON message into a binary frame
        /// </summary>
        /// <param name="jsonMessage">JSON message to encode</param>
        /// <returns>Binary frame bytes</returns>
        public byte[] EncodeMessage(string jsonMessage) {
            try {
                return _framer.EncodeMessage(jsonMessage);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to encode JSON message to binary frame: {MessageLength} chars", jsonMessage.Length);
                throw;
            }
        }

        /// <summary>
        /// Get current protocol statistics
        /// </summary>
        public ProtocolStatistics Statistics => _statistics;

        /// <summary>
        /// Get current framer state for debugging
        /// </summary>
        public ReceiveState CurrentState => _framer.CurrentState;

        public void Dispose() {
            // No resources to dispose - centralized logging service handles cleanup
        }
    }
}
