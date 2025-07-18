using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Configuration;
using UniMixerServer.Models;
using UniMixerServer.Communication.MessageProcessing;
using UniMixerServer.Communication.BinaryProtocol;
using UniMixerServer.Services;

namespace UniMixerServer.Communication {
    /// <summary>
    /// Serial handler using binary framed protocol with automatic fallback to text protocol
    /// </summary>
    public class SerialHandler : BaseCommunicationHandler {
        private readonly SerialConfig _config;
        private SerialPort? _serialPort;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _readTask;
        private Task? _statisticsTask;

        // Binary protocol support
        private BinaryMessageProcessor? _binaryMessageProcessor;
        private bool _useBinaryProtocol;
        private bool _protocolDetected;
        private readonly StringBuilder _textBuffer = new StringBuilder();

        // Exception decoder
        private readonly EspExceptionDecoder _exceptionDecoder;

        public override string Name => "Serial";
        public override bool IsConnected => _serialPort?.IsOpen ?? false;

        public SerialHandler(ILogger<SerialHandler> logger, SerialConfig config, BinaryMessageProcessor binaryMessageProcessor, EspExceptionDecoder exceptionDecoder)
            : base(logger, binaryMessageProcessor) {
            _config = config;
            _useBinaryProtocol = config.BinaryProtocol.EnableBinaryProtocol;
            _binaryMessageProcessor = binaryMessageProcessor;
            _exceptionDecoder = exceptionDecoder;
        }



        private async Task LogStatisticsAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                try {
                    await Task.Delay(_config.BinaryProtocol.StatisticsLogIntervalMs, cancellationToken);

                    if (_binaryMessageProcessor != null && _config.BinaryProtocol.EnableDetailedLogging) {
                        _logger.LogInformation("Protocol statistics: {Statistics}",
                            _binaryMessageProcessor.Statistics.GetSummary());
                    }
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error logging protocol statistics");
                }
            }
        }

        public override Task StartAsync(CancellationToken cancellationToken = default) {
            try {
                _logger.LogInformation("Starting Serial handler on port {Port}...", _config.PortName);

                _serialPort = new SerialPort {
                    PortName = _config.PortName,
                    BaudRate = _config.BaudRate,
                    DataBits = _config.DataBits,
                    Parity = ParseParity(_config.Parity),
                    StopBits = ParseStopBits(_config.StopBits),
                    ReadTimeout = _config.ReadTimeoutMs,
                    WriteTimeout = _config.WriteTimeoutMs,
                    Encoding = Encoding.UTF8
                };

                _serialPort.Open();

                _cancellationTokenSource = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadSerialDataAsync(_cancellationTokenSource.Token), cancellationToken);

                // Start statistics logging task if detailed logging enabled
                if (_useBinaryProtocol && _config.BinaryProtocol.EnableDetailedLogging &&
                    _config.BinaryProtocol.StatisticsLogIntervalMs > 0) {
                    _statisticsTask = Task.Run(() => LogStatisticsAsync(_cancellationTokenSource.Token), cancellationToken);
                }

                _logger.LogInformation("Serial handler started successfully on {Port}", _config.PortName);
                NotifyConnectionStatusChanged(true, $"Connected to serial port {_config.PortName}");

                // Log session start for binary data debugging
                BinaryDataLogger.LogSessionStart($"Serial Port {_config.PortName}");

                return Task.CompletedTask;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to start Serial handler");
                NotifyConnectionStatusChanged(false, $"Failed to connect to serial port: {ex.Message}");
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken = default) {
            try {
                _logger.LogInformation("Stopping Serial handler...");

                _cancellationTokenSource?.Cancel();

                // Wait for all tasks to complete
                var tasks = new List<Task>();
                if (_readTask != null) tasks.Add(_readTask);
                if (_statisticsTask != null) tasks.Add(_statisticsTask);

                if (tasks.Count > 0) {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }

                if (_serialPort?.IsOpen == true) {
                    _serialPort.Close();
                }

                _serialPort?.Dispose();
                _serialPort = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                // Log final statistics if available (only if detailed logging enabled)
                if (_useBinaryProtocol && _binaryMessageProcessor != null && _config.BinaryProtocol.EnableDetailedLogging) {
                    _logger.LogInformation("Final protocol statistics: {Statistics}",
                        _binaryMessageProcessor.Statistics.GetSummary());
                }

                _logger.LogInformation("Serial handler stopped successfully");
                NotifyConnectionStatusChanged(false, "Disconnected from serial port");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error stopping Serial handler");
                throw;
            }
        }

        public override async Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default) {
            if (!IsConnected) {
                _logger.LogWarning("Cannot send status - serial port not connected");
                return;
            }

            // Don't send messages if crash detection is active
            if (_exceptionDecoder.IsCrashDetectionActive) {
                _logger.LogDebug("Crash detection is active, not sending status message");
                return;
            }

            try {
                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogDebug("Sending status: {SessionCount} sessions, {Length} chars", status.Sessions.Count, json.Length);

                // Log outgoing data

                if (_useBinaryProtocol && _binaryMessageProcessor != null) {
                    // Send as binary frame
                    var binaryFrame = _binaryMessageProcessor.EncodeMessage(json);
                    // For debugging/logging, show the JSON that was encoded
                    OutgoingDataLogger.LogOutgoingData(json, "Serial");
                    // Log the binary frame as hex string
                    var hexString = BitConverter.ToString(binaryFrame).Replace("-", " ");
                    OutgoingDataLogger.LogOutgoingData("Binary frame (hex): {HexString}", hexString);
                    await Task.Run(() => _serialPort!.Write(binaryFrame, 0, binaryFrame.Length), cancellationToken);
                }
                else {
                    // Send as text with newline
                    var message = $"{json}\n";
                    await Task.Run(() => _serialPort!.Write(message), cancellationToken);
                }

                _logger.LogDebug("Status message sent via serial port");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending status message via serial port");
            }
        }

        public override async Task SendAssetAsync(AssetResponse assetResponse, CancellationToken cancellationToken = default) {
            if (!IsConnected) {
                _logger.LogWarning("Cannot send asset - serial port not connected");
                return;
            }

            // Don't send messages if crash detection is active
            if (_exceptionDecoder.IsCrashDetectionActive) {
                _logger.LogDebug("Crash detection is active, not sending asset");
                return;
            }

            try {
                // For serial communication, we'll send asset data as base64 encoded JSON
                // Create a serializable version with base64 encoded asset data
                var response = new {
                    assetResponse.MessageType,
                    assetResponse.RequestId,
                    assetResponse.DeviceId,
                    assetResponse.ProcessName,
                    // omit unused metadata
                    // assetResponse.Metadata,
                    AssetData = assetResponse.AssetData != null ? Convert.ToBase64String(assetResponse.AssetData) : null,
                    assetResponse.Success,
                    assetResponse.ErrorMessage
                };

                var json = JsonSerializer.Serialize(response, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                _logger.LogDebug("Sending asset: {ProcessName}, {Size} bytes, {MessageLength} chars",
                    assetResponse.ProcessName,
                    assetResponse.AssetData?.Length ?? 0,
                    json.Length);

                // Log outgoing data
                OutgoingDataLogger.LogOutgoingData(json, "Serial");

                if (_useBinaryProtocol && _binaryMessageProcessor != null) {
                    // Send as binary frame
                    var binaryFrame = _binaryMessageProcessor.EncodeMessage(json);

                    await Task.Run(() => _serialPort!.Write(binaryFrame, 0, binaryFrame.Length), cancellationToken);
                }
                else {
                    // Send as text with newline
                    var message = $"{json}\n";
                    await Task.Run(() => _serialPort!.Write(message), cancellationToken);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending asset response via serial port");
            }
        }

        public override async Task SendPingResponseAsync(string pongJson, CancellationToken cancellationToken = default) {
            if (!IsConnected) {
                _logger.LogWarning("Cannot send ping response - serial port not connected");
                return;
            }

            // Don't send messages if crash detection is active
            if (_exceptionDecoder.IsCrashDetectionActive) {
                _logger.LogDebug("Crash detection is active, not sending ping response");
                return;
            }

            try {
                _logger.LogDebug("Sending ping response: {Length} chars", pongJson.Length);

                // Log outgoing data
                OutgoingDataLogger.LogOutgoingData(pongJson, "Serial");

                if (_useBinaryProtocol && _binaryMessageProcessor != null) {
                    // Send as binary frame
                    var binaryFrame = _binaryMessageProcessor.EncodeMessage(pongJson);
                    await Task.Run(() => _serialPort!.Write(binaryFrame, 0, binaryFrame.Length), cancellationToken);
                }
                else {
                    // Send as text with newline
                    var message = $"{pongJson}\n";
                    await Task.Run(() => _serialPort!.Write(message), cancellationToken);
                }

                _logger.LogDebug("Ping response sent via serial port");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending ping response via serial port");
            }
        }

        private async Task ReadSerialDataAsync(CancellationToken cancellationToken) {
            if (_useBinaryProtocol && _binaryMessageProcessor != null) {
                await ReadBinaryDataAsync(cancellationToken);
            }
            else {
                throw new InvalidOperationException("Text protocol is not supported in this implementation");
                await ReadTextDataAsync(cancellationToken);
            }
        }

        private async Task ReadBinaryDataAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested && IsConnected) {
                try {
                    if (_serialPort!.BytesToRead > 0) {
                        // ALWAYS read available bytes - crash detection needs data to complete
                        var availableBytes = _serialPort.BytesToRead;
                        var buffer = new byte[availableBytes];
                        var bytesRead = _serialPort.Read(buffer, 0, availableBytes);

                        if (bytesRead > 0) {
                            var readBytes = new byte[bytesRead];
                            Array.Copy(buffer, readBytes, bytesRead);

                            _logger.LogTrace("🔍 SerialHandler read {Length} bytes", bytesRead);

                            // SINGLE POINT OF EXCEPTION DETECTION
                            // ALWAYS check raw serial data for ESP32 crashes - this feeds ongoing crash detection
                            try {
                                var rawString = System.Text.Encoding.UTF8.GetString(readBytes);
                                _logger.LogTrace("🔍 Feeding data to ESP32 crash detector");
                                
                                if (_exceptionDecoder.ProcessSerialData(rawString)) {
                                    _logger.LogCritical("🚨 ESP32 CRASH PROCESSING COMPLETED - SerialHandler exiting");
                                    
                                    return; // Crash processing completed, exception decoder will handle exit
                                }
                            }
                            catch (Exception ex) {
                                _logger.LogTrace(ex, "Failed to check raw serial data for crashes");
                            }

                            // If crash detection is active, DON'T pass to binary processor
                            // Just continue feeding data to the crash detector
                            if (_exceptionDecoder.IsCrashDetectionActive) {
                                _logger.LogTrace("🔍 Crash detection active, skipping binary processing, continuing data feed");
                                continue; // Skip binary processing, keep feeding crash detector
                            }

                            // Log raw binary data for debugging
                            BinaryDataLogger.LogBinaryData(readBytes, "Serial");

                            // CLEAN DATA ONLY: Pass to binary processor for normal protocol handling
                            _logger.LogTrace("🔍 No crash detected, passing clean data to BinaryMessageProcessor");
                            await _binaryMessageProcessor!.ProcessBinaryAsync(readBytes, "Serial");

                            // Handle protocol auto-detection on first successful decode
                            if (!_protocolDetected && _binaryMessageProcessor.Statistics.MessagesReceived > 0) {
                                _protocolDetected = true;
                                _logger.LogTrace("Binary protocol detected and working correctly");
                            }
                        }
                    }

                    await Task.Delay(5, cancellationToken); // Shorter delay for binary processing
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error reading binary data from serial port");

                    // Handle protocol fallback if enabled
                    if (_config.BinaryProtocol.EnableProtocolAutoDetection && !_protocolDetected) {
                        _logger.LogTrace("Binary protocol failed, attempting fallback to text protocol");
                        _useBinaryProtocol = false;
                        _textBuffer.Clear();
                        return; // Exit and let ReadSerialDataAsync restart with text mode
                    }

                    if (_config.EnableAutoReconnect) {
                        await Task.Delay(_config.ReconnectDelayMs, cancellationToken);
                        await TryReconnectAsync();
                    }
                    else {
                        break;
                    }
                }
            }
        }

        private async Task ReadTextDataAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested && IsConnected) {
                try {
                    if (_serialPort!.BytesToRead > 0) {
                        var data = _serialPort.ReadExisting();
                        _textBuffer.Append(data);

                        var content = _textBuffer.ToString();

                        // Check for ESP32 crashes before processing regular messages
                        if (_exceptionDecoder.ProcessSerialData(data)) {
                            // A crash was detected and decoded, the decoder will handle exiting
                            return;
                        }

                        // Process ESP32 custom format: ~prefix{JSON}]
                        await ProcessEsp32CustomFormat(content);

                        // Also process standard newline-delimited JSON for compatibility
                        await ProcessStandardTextFormat(content);
                    }

                    await Task.Delay(10, cancellationToken);
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error reading text data from serial port");

                    if (_config.EnableAutoReconnect) {
                        await Task.Delay(_config.ReconnectDelayMs, cancellationToken);
                        await TryReconnectAsync();
                    }
                    else {
                        break;
                    }
                }
            }
        }

        private async Task ProcessEsp32CustomFormat(string content) {
            // Process ESP32 custom format: ~prefix{JSON}]
            int startIndex = 0;
            while (true) {
                // Find start marker ~
                int startMarker = content.IndexOf('~', startIndex);
                if (startMarker == -1) {
                    break;
                }

                // Find end marker ]
                int endMarker = content.IndexOf(']', startMarker);
                if (endMarker == -1) {
                    // Incomplete message, keep from start marker onwards
                    _textBuffer.Clear();
                    _textBuffer.Append(content.Substring(startMarker));
                    break;
                }

                // Extract the complete message
                var messageFrame = content.Substring(startMarker, endMarker - startMarker + 1);

                // Extract JSON payload (skip ~ and variable prefix, remove ])
                int jsonStart = messageFrame.IndexOf('{');
                if (jsonStart != -1) {
                    var jsonPayload = messageFrame.Substring(jsonStart, messageFrame.Length - jsonStart - 1); // Remove ]

                    if (!string.IsNullOrWhiteSpace(jsonPayload)) {
                        _logger.LogTrace("Processing ESP32 message: {Frame} -> JSON: {Json}", messageFrame, jsonPayload);
                        await ProcessIncomingDataAsync(jsonPayload, "Serial");
                    }
                }

                startIndex = endMarker + 1;
            }

            // Remove processed content
            if (startIndex > 0) {
                var remaining = content.Substring(startIndex);
                _textBuffer.Clear();
                _textBuffer.Append(remaining);
            }
        }

        private async Task ProcessStandardTextFormat(string content) {
            // Process standard newline-delimited JSON for compatibility
            var lines = content.Split('\n');

            // Process all complete lines except the last one
            for (int i = 0; i < lines.Length - 1; i++) {
                var line = lines[i].Trim('\r', '\n');
                if (!string.IsNullOrWhiteSpace(line) && !line.Contains('~') && !line.Contains(']')) {
                    // Only process if it doesn't look like ESP32 format
                    await ProcessIncomingDataAsync(line, "Serial");
                }
            }

            // Keep the last incomplete line in buffer only if it's not ESP32 format
            var lastLine = lines[lines.Length - 1];
            if (!lastLine.Contains('~')) {
                _textBuffer.Clear();
                _textBuffer.Append(lastLine);
            }
        }

        private async Task TryReconnectAsync() {
            try {
                _logger.LogInformation("Attempting to reconnect to serial port {Port}...", _config.PortName);

                if (_serialPort?.IsOpen == true) {
                    _serialPort.Close();
                }

                _serialPort?.Dispose();
                _serialPort = new SerialPort {
                    PortName = _config.PortName,
                    BaudRate = _config.BaudRate,
                    DataBits = _config.DataBits,
                    Parity = ParseParity(_config.Parity),
                    StopBits = ParseStopBits(_config.StopBits),
                    ReadTimeout = _config.ReadTimeoutMs,
                    WriteTimeout = _config.WriteTimeoutMs,
                    Encoding = Encoding.UTF8
                };

                _serialPort.Open();

                _logger.LogInformation("Successfully reconnected to serial port {Port}", _config.PortName);
                NotifyConnectionStatusChanged(true, $"Reconnected to serial port {_config.PortName}");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to reconnect to serial port");
                NotifyConnectionStatusChanged(false, $"Failed to reconnect: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        private static Parity ParseParity(string parity) {
            return parity.ToUpperInvariant() switch {
                "NONE" => Parity.None,
                "ODD" => Parity.Odd,
                "EVEN" => Parity.Even,
                "MARK" => Parity.Mark,
                "SPACE" => Parity.Space,
                _ => Parity.None
            };
        }

        private static StopBits ParseStopBits(string stopBits) {
            return stopBits.ToUpperInvariant() switch {
                "NONE" => StopBits.None,
                "ONE" => StopBits.One,
                "TWO" => StopBits.Two,
                "ONEPOINTFIVE" => StopBits.OnePointFive,
                _ => StopBits.One
            };
        }

        protected override void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                    _cancellationTokenSource?.Cancel();
                    _readTask?.Wait(1000);
                    _serialPort?.Dispose();
                    _cancellationTokenSource?.Dispose();
                }
                base.Dispose(disposing);
                _disposed = true;
            }
        }
    }
}
