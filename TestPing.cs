using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Communication.MessageProcessing;
using UniMixerServer.Models;
using UniMixerServer.Services;

namespace UniMixerServer {
    /// <summary>
    /// Simple test class to verify ping functionality
    /// </summary>
    public static class TestPing {
        public static async Task RunPingTest() {
            Console.WriteLine("=== Ping Service Test ===");
            
            // Setup logger
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<PingService>();
            
            // Create ping service
            var pingService = new PingService(logger);
            
            // Create a mock ping request message
            var pingRequestJson = JsonSerializer.Serialize(new {
                messageType = MessageTypes.PING_REQUEST,
                deviceId = "ESP32S3-TEST-DEVICE",
                requestId = "test_ping_001",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                esp_timestamp_us = 123456789, // Mock ESP timestamp
                server_unix_time = 0,         // Will be filled by server
                sequence = 1                  // Ping sequence number
            });
            
            Console.WriteLine($"Mock ping request: {pingRequestJson}");
            
            // Parse the message
            var jsonDoc = JsonDocument.Parse(pingRequestJson);
            var parsedMessage = new ParsedMessage {
                MessageType = MessageTypes.PING_REQUEST,
                Data = jsonDoc.RootElement,
                SourceInfo = "Test Client"
            };
            
            // Process the ping request
            Console.WriteLine("\nProcessing ping request...");
            var pongResponse = await pingService.ProcessPingRequestAsync(parsedMessage);
            
            Console.WriteLine($"Pong response: {pongResponse}");
            
            // Verify the response contains expected fields
            var pongDoc = JsonDocument.Parse(pongResponse);
            var pongRoot = pongDoc.RootElement;
            
            Console.WriteLine("\n=== Verification ===");
            Console.WriteLine($"✓ Message Type: {pongRoot.GetProperty("messageType").GetString()}");
            Console.WriteLine($"✓ Device ID: {pongRoot.GetProperty("deviceId").GetString()}");
            Console.WriteLine($"✓ ESP Timestamp: {pongRoot.GetProperty("esp_timestamp_us").GetUInt32()}");
            Console.WriteLine($"✓ Server Unix Time: {pongRoot.GetProperty("server_unix_time").GetInt64()}");
            Console.WriteLine($"✓ Sequence: {pongRoot.GetProperty("sequence").GetUInt32()}");
            
            // Verify server timestamp is reasonable (within last few seconds)
            var serverUnixTime = pongRoot.GetProperty("server_unix_time").GetInt64();
            var currentUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeDiff = Math.Abs(currentUnixTime - serverUnixTime);
            
            if (timeDiff <= 5) {
                Console.WriteLine($"✓ Server timestamp is current (diff: {timeDiff}s)");
            } else {
                Console.WriteLine($"✗ Server timestamp seems incorrect (diff: {timeDiff}s)");
            }
            
            Console.WriteLine("\n=== Test Complete ===");
        }
    }
}