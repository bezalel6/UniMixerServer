using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Models;
using UniMixerServer.Services;
using UniMixerServer.Communication.MessageProcessing;

namespace UniMixerServer.Services {
    /// <summary>
    /// Service for handling ping requests and generating pong responses
    /// Provides latency measurement and time synchronization capabilities
    /// </summary>
    public class PingService {
        private readonly ILogger<PingService> _logger;

        public PingService(ILogger<PingService> logger) {
            _logger = logger;
        }

        /// <summary>
        /// Process a ping request and generate a pong response
        /// </summary>
        /// <param name="message">The parsed ping request message</param>
        /// <returns>JSON pong response with Unix timestamp</returns>
        public async Task<string> ProcessPingRequestAsync(ParsedMessage message) {
            try {
                var data = message.Data;
                
                // Extract ping data from request
                var espTimestamp = data.TryGetProperty("esp_timestamp_us", out var espTimestampProp) 
                    ? espTimestampProp.GetUInt32() : 0;
                var sequence = data.TryGetProperty("sequence", out var sequenceProp) 
                    ? sequenceProp.GetUInt32() : 0;
                var deviceId = data.TryGetProperty("deviceId", out var deviceIdProp) 
                    ? deviceIdProp.GetString() : "";
                var requestId = data.TryGetProperty("requestId", out var requestIdProp) 
                    ? requestIdProp.GetString() : "";

                // Get current Unix timestamp (seconds since epoch)
                var currentUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Create pong response
                var pongResponse = new {
                    messageType = MessageTypes.PONG_RESPONSE,
                    deviceId = deviceId,
                    requestId = requestId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    esp_timestamp_us = espTimestamp,  // Echo back the original ESP timestamp
                    server_unix_time = currentUnixTime,  // Server's Unix timestamp
                    sequence = sequence  // Echo back the sequence number
                };

                var responseJson = JsonSerializer.Serialize(pongResponse);
                
                _logger.LogDebug("Processed ping request from {DeviceId}, sequence {Sequence}, responding with Unix time {UnixTime}",
                    deviceId, sequence, currentUnixTime);

                return responseJson;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing ping request from {Source}", message.SourceInfo);
                throw;
            }
        }

        /// <summary>
        /// Handle ping request message and send pong response
        /// </summary>
        /// <param name="message">The parsed ping request message</param>
        /// <param name="sendResponse">Callback to send the response</param>
        public async Task HandlePingRequestAsync(ParsedMessage message, Func<string, Task> sendResponse) {
            try {
                var pongResponse = await ProcessPingRequestAsync(message);
                await sendResponse(pongResponse);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to handle ping request from {Source}", message.SourceInfo);
            }
        }
    }
}