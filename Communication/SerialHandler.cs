using System;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Configuration;
using UniMixerServer.Models;
using UniMixerServer.Communication.MessageProcessing;
using UniMixerServer.Services;

namespace UniMixerServer.Communication {
    /// <summary>
    /// Serial handler using O(1) message processing
    /// </summary>
    public class SerialHandler : BaseCommunicationHandler {
        private readonly SerialConfig _config;
        private SerialPort? _serialPort;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _readTask;

        public override string Name => "Serial";
        public override bool IsConnected => _serialPort?.IsOpen ?? false;

        public SerialHandler(ILogger<SerialHandler> logger, SerialConfig config, JsonMessageProcessor messageProcessor)
            : base(logger, messageProcessor) {
            _config = config;
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

                _logger.LogInformation("Serial handler started successfully on {Port}", _config.PortName);
                NotifyConnectionStatusChanged(true, $"Connected to serial port {_config.PortName}");

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

                if (_readTask != null) {
                    await _readTask.ConfigureAwait(false);
                }

                if (_serialPort?.IsOpen == true) {
                    _serialPort.Close();
                }

                _serialPort?.Dispose();
                _serialPort = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

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
                _logger.LogWarning("Cannot send status - Serial port not connected");
                return;
            }

            try {
                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                var message = $"{json}\n";

                var logMessage = $"Sending status: {status.Sessions.Count} sessions, {message.Length} chars (Reason: {status.Reason}";
                if (!string.IsNullOrEmpty(status.OriginatingDeviceId)) {
                    logMessage += $", OriginatingDevice: {status.OriginatingDeviceId}";
                }
                if (!string.IsNullOrEmpty(status.OriginatingRequestId)) {
                    logMessage += $", RequestId: {status.OriginatingRequestId}";
                }
                logMessage += ")";

                _logger.LogDebug(logMessage);

                // Log outgoing data
                OutgoingDataLogger.LogOutgoingData(message.TrimEnd('\n'), "Serial");

                await Task.Run(() => _serialPort!.Write(message), cancellationToken);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending status message via serial port");
            }
        }

        public override async Task SendAssetAsync(AssetResponse assetResponse, CancellationToken cancellationToken = default) {
            if (!IsConnected) {
                _logger.LogWarning("Cannot send asset - Serial port not connected");
                return;
            }

            try {
                // For serial communication, we'll send asset data as base64 encoded JSON
                var response = new {
                    assetResponse.MessageType,
                    assetResponse.RequestId,
                    assetResponse.DeviceId,
                    assetResponse.ProcessName,
                    assetResponse.Metadata,
                    AssetData = assetResponse.AssetData != null ? Convert.ToBase64String(assetResponse.AssetData) : null,
                    assetResponse.Success,
                    assetResponse.ErrorMessage
                };

                var json = JsonSerializer.Serialize(response, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                var message = $"{json}\n";

                _logger.LogDebug("Sending asset: {ProcessName}, {Size} bytes, {MessageLength} chars",
                    assetResponse.ProcessName,
                    assetResponse.AssetData?.Length ?? 0,
                    message.Length);

                // Log outgoing data
                OutgoingDataLogger.LogOutgoingData(message.TrimEnd('\n'), "Serial");

                await Task.Run(() => _serialPort!.Write(message), cancellationToken);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending asset response via serial port");
            }
        }

        private async Task ReadSerialDataAsync(CancellationToken cancellationToken) {
            var buffer = new StringBuilder();

            while (!cancellationToken.IsCancellationRequested && IsConnected) {
                try {
                    if (_serialPort!.BytesToRead > 0) {
                        var data = _serialPort.ReadExisting();
                        buffer.Append(data);

                        // Process complete lines (bus-specific parsing)
                        var content = buffer.ToString();
                        var lines = content.Split('\n');

                        // Process all complete lines except the last one
                        for (int i = 0; i < lines.Length - 1; i++) {
                            var line = lines[i].Trim('\r', '\n');
                            if (!string.IsNullOrWhiteSpace(line)) {
                                // Use O(1) message processing
                                await ProcessIncomingDataAsync(line, "Serial");
                            }
                        }

                        // Keep the last incomplete line in buffer
                        buffer.Clear();
                        buffer.Append(lines[lines.Length - 1]);
                    }

                    await Task.Delay(10, cancellationToken);
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error reading from serial port");

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
