using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Models;
using System.Collections.Generic;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// Shared utility for parsing JSON messages and dispatching to handlers
    /// Eliminates code duplication between BinaryMessageProcessor and JsonMessageProcessor
    /// </summary>
    public static class JsonMessageParser {
        /// <summary>
        /// Parse JSON message and dispatch to appropriate handler
        /// </summary>
        /// <param name="jsonMessage">The JSON message to parse</param>
        /// <param name="sourceInfo">Source information for logging</param>
        /// <param name="handlers">Dictionary of registered handlers</param>
        /// <param name="logger">Logger instance</param>
        /// <returns>True if message was successfully parsed and handled</returns>
        public static async Task<bool> ParseAndDispatchAsync(
            string jsonMessage, 
            string sourceInfo, 
            Dictionary<MessageType, MessageHandler> handlers, 
            ILogger logger) {
            
            if (string.IsNullOrWhiteSpace(jsonMessage)) {
                return false;
            }

            try {
                // Step 1: Parse JSON
                var jsonDoc = JsonDocument.Parse(jsonMessage);
                var root = jsonDoc.RootElement;

                // Step 2: Extract and validate message type
                var messageType = ExtractMessageType(root, sourceInfo, logger);
                if (messageType == MessageType.INVALID) {
                    return false;
                }

                // Step 3: Find handler (O(1) lookup)
                if (!handlers.TryGetValue(messageType, out var handler)) {
                    logger.LogDebug("No handler registered for {MessageType} from {Source}", messageType, sourceInfo);
                    return false;
                }

                // Step 4: Create message and dispatch
                var parsedMessage = new ParsedMessage {
                    MessageType = messageType,
                    Data = root,
                    SourceInfo = sourceInfo
                };

                logger.LogDebug("Processing {MessageType} from {Source}", messageType, sourceInfo);
                await handler(parsedMessage);
                return true;
            }
            catch (JsonException ex) {
                logger.LogDebug("Invalid JSON from {Source}: {Error}", sourceInfo, ex.Message);
                return false;
            }
            catch (Exception ex) {
                logger.LogError(ex, "Error processing message from {Source}: {Length} chars", sourceInfo, jsonMessage.Length);
                return false;
            }
        }

        /// <summary>
        /// Extract MessageType from JSON element with support for both string and numeric types
        /// </summary>
        private static MessageType ExtractMessageType(JsonElement root, string sourceInfo, ILogger logger) {
            if (!root.TryGetProperty("messageType", out var messageTypeElement)) {
                logger.LogDebug("No messageType property in JSON from {Source}", sourceInfo);
                return MessageType.INVALID;
            }

            // Handle string messageType (legacy support)
            if (messageTypeElement.ValueKind == JsonValueKind.String) {
                if (Enum.TryParse<MessageType>(messageTypeElement.GetString(), true, out var stringMessageType)) {
                    return stringMessageType;
                }
                logger.LogDebug("Invalid messageType string '{Value}' from {Source}", messageTypeElement.GetString(), sourceInfo);
                return MessageType.INVALID;
            }

            // Handle numeric messageType (preferred)
            if (messageTypeElement.ValueKind == JsonValueKind.Number) {
                var messageTypeValue = messageTypeElement.GetInt32();
                if (Enum.IsDefined(typeof(MessageType), messageTypeValue)) {
                    return (MessageType)messageTypeValue;
                }
                logger.LogDebug("Invalid messageType number {Value} from {Source}", messageTypeValue, sourceInfo);
                return MessageType.INVALID;
            }

            logger.LogDebug("Invalid messageType property type from {Source}", sourceInfo);
            return MessageType.INVALID;
        }
    }
}