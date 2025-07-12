using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Models;

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
            Dictionary<string, MessageHandler> handlers, 
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
                if (string.IsNullOrEmpty(messageType)) {
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
        /// Extract message type string from JSON element
        /// </summary>
        private static string ExtractMessageType(JsonElement root, string sourceInfo, ILogger logger) {
            if (!root.TryGetProperty("messageType", out var messageTypeElement)) {
                logger.LogDebug("No messageType property in JSON from {Source}", sourceInfo);
                return string.Empty;
            }

            // Handle string messageType
            if (messageTypeElement.ValueKind == JsonValueKind.String) {
                var messageType = messageTypeElement.GetString();
                if (!string.IsNullOrEmpty(messageType)) {
                    return messageType;
                }
                logger.LogDebug("Empty messageType string from {Source}", sourceInfo);
                return string.Empty;
            }

            // Handle numeric messageType (legacy support)
            if (messageTypeElement.ValueKind == JsonValueKind.Number) {
                var messageTypeValue = messageTypeElement.GetInt32();
                // Convert legacy numeric types to strings
                var messageType = messageTypeValue switch {
                    1 => MessageTypes.STATUS_UPDATE,
                    2 => MessageTypes.STATUS_MESSAGE, 
                    3 => MessageTypes.GET_STATUS,
                    4 => MessageTypes.GET_ASSETS,
                    5 => MessageTypes.ASSET_RESPONSE,
                    6 => MessageTypes.SESSION_UPDATE,   
                    _ => string.Empty
                };
                
                if (!string.IsNullOrEmpty(messageType)) {
                    logger.LogDebug("Converted legacy numeric messageType {Value} to {MessageType} from {Source}", 
                        messageTypeValue, messageType, sourceInfo);
                    return messageType;
                }
                logger.LogDebug("Invalid messageType number {Value} from {Source}", messageTypeValue, sourceInfo);
                return string.Empty;
            }

            logger.LogDebug("Invalid messageType property type from {Source}", sourceInfo);
            return string.Empty;
        }
    }
}