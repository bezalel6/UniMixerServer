using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Configuration;
using UniMixerServer.Models;

namespace UniMixerServer.Communication {
    public class SerialHandler : ICommunicationHandler, IDisposable {
        private readonly ILogger<SerialHandler> _logger;
        private readonly SerialConfig _config;
        private SerialPort? _serialPort;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _readTask;
        private bool _disposed = false;

        public string Name => "Serial";
        public bool IsConnected => _serialPort?.IsOpen ?? false;

        public event EventHandler<StatusUpdateReceivedEventArgs>? StatusUpdateReceived;
        public event EventHandler<StatusRequestReceivedEventArgs>? StatusRequestReceived;
        public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        public SerialHandler(ILogger<SerialHandler> logger, SerialConfig config) {
            _logger = logger;
            _config = config;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) {
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

                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                    IsConnected = true,
                    HandlerName = Name,
                    Message = $"Connected to serial port {_config.PortName}"
                });

                return Task.CompletedTask;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to start Serial handler");

                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                    IsConnected = false,
                    HandlerName = Name,
                    Message = $"Failed to connect to serial port: {ex.Message}"
                });

                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default) {
            try {
                _logger.LogInformation("Stopping Serial handler...");

                // Cancel the read task
                _cancellationTokenSource?.Cancel();

                // Wait for read task to complete
                if (_readTask != null) {
                    await _readTask.ConfigureAwait(false);
                }

                // Close the serial port
                if (_serialPort?.IsOpen == true) {
                    _serialPort.Close();
                }

                _serialPort?.Dispose();
                _serialPort = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.LogInformation("Serial handler stopped successfully");

                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                    IsConnected = false,
                    HandlerName = Name,
                    Message = "Disconnected from serial port"
                });
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error stopping Serial handler");
                throw;
            }
        }

        public async Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default) {
            if (!IsConnected) {
                _logger.LogWarning("Cannot send status - Serial port not connected");
                return;
            }

            try {
                // Create a clean JSON structure that matches what the ESP32 expects
                var sessionsList = new List<object>();
                foreach (var session in status.Sessions) {
                    var sessionDict = new Dictionary<string, object> {
                        ["processName"] = session.ProcessName ?? string.Empty,
                        ["volume"] = session.Volume,
                        ["isMuted"] = session.IsMuted,
                        ["state"] = session.State ?? string.Empty,
                    };

                    sessionsList.Add(sessionDict);
                }

                var statusData = new Dictionary<string, object> {
                    ["sessions"] = sessionsList,
                };
                if (status.DefaultDevice != null) {
                    var defaultDeviceData = new Dictionary<string, object> {
                        ["friendlyName"] = status.DefaultDevice.FriendlyName ?? string.Empty,
                        ["volume"] = status.DefaultDevice.Volume,
                        ["isMuted"] = status.DefaultDevice.IsMuted,
                        ["dataFlow"] = status.DefaultDevice.DataFlow ?? string.Empty,
                        ["deviceRole"] = status.DefaultDevice.DeviceRole ?? string.Empty
                    };
                    statusData.Add("defaultDevice", defaultDeviceData);
                }
                var json = JsonSerializer.Serialize(statusData, new JsonSerializerOptions {
                    // WriteIndented = false,
                    // Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                var message = $"{json}\n";

                // Concise logging - only essential info
                _logger.LogDebug("Sending status: {SessionCount} sessions, {MessageLength} chars",
                    sessionsList.Count, message.Length);
                _logger.LogDebug(json);
                await Task.Run(() => _serialPort!.Write(message), cancellationToken);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending status message via serial port");
            }
        }

        private async Task ReadSerialDataAsync(CancellationToken cancellationToken) {
            var buffer = new StringBuilder();

            while (!cancellationToken.IsCancellationRequested && IsConnected) {
                try {
                    if (_serialPort!.BytesToRead > 0) {
                        var data = _serialPort.ReadExisting();
                        buffer.Append(data);

                        // Process complete lines
                        var content = buffer.ToString();
                        var lines = content.Split('\n');

                        // Process all complete lines (all but the last one)
                        for (int i = 0; i < lines.Length - 1; i++) {
                            var line = lines[i].Trim('\r', '\n');
                            if (!string.IsNullOrWhiteSpace(line)) {
                                // _logger.LogDebug("Processing message: {Length} chars", line.Length);
                                _logger.LogDebug(line);
                                await ProcessSerialMessage(line);
                            }
                        }

                        // Keep the last incomplete line in buffer
                        buffer.Clear();
                        buffer.Append(lines[lines.Length - 1]);
                    }

                    await Task.Delay(10, cancellationToken); // Small delay to prevent busy waiting
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

        private async Task ProcessSerialMessage(string message) {
            try {
                // First try to parse as a status update (new protocol)
                var statusUpdate = TryParseStatusUpdate(message);
                if (statusUpdate != null) {
                    _logger.LogInformation("StatusUpdate from ESP32: {SessionCount} sessions",
                        statusUpdate.Sessions.Count);
                    StatusUpdateReceived?.Invoke(this, new StatusUpdateReceivedEventArgs {
                        StatusUpdate = statusUpdate,
                        Source = "Serial",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    return;
                }

                // Try to parse as a status request
                var statusRequest = TryParseStatusRequest(message);
                if (statusRequest != null) {
                    _logger.LogInformation("StatusRequest from ESP32 - triggering status broadcast");

                    StatusRequestReceived?.Invoke(this, new StatusRequestReceivedEventArgs {
                        StatusRequest = statusRequest,
                        Source = "Serial",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    return;
                }

                _logger.LogWarning("Failed to parse message from {Length} char message", message.Length);
            }
            catch (Exception) {
                _logger.LogInformation(message);
                // _logger.LogError(ex, "Error processing serial message: {Length} chars", message.Length);
            }

            await Task.CompletedTask;
        }

        private StatusUpdate? TryParseStatusUpdate(string message) {
            try {
                var update = JsonSerializer.Deserialize<StatusUpdate>(message, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                _logger.LogInformation("Deserialized StatusUpdate: {StatusUpdate}", JsonSerializer.Serialize(update, new JsonSerializerOptions { WriteIndented = true }));
                // Check if it's a valid status update
                if (update?.MessageType == "StatusUpdate") {
                    return update;
                }
            }
            catch {
                // Not a status update, continue
            }
            return null;
        }

        private StatusRequest? TryParseStatusRequest(string message) {
            try {
                var request = JsonSerializer.Deserialize<StatusRequest>(message, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                // Check if it's a valid status request
                if (request?.MessageType == "GetStatus") {
                    return request;
                }
            }
            catch {
                // Not a status request, continue
            }
            return null;
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

                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                    IsConnected = true,
                    HandlerName = Name,
                    Message = $"Reconnected to serial port {_config.PortName}"
                });
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to reconnect to serial port");

                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                    IsConnected = false,
                    HandlerName = Name,
                    Message = $"Failed to reconnect: {ex.Message}"
                });
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

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                    _cancellationTokenSource?.Cancel();
                    _readTask?.Wait(1000);
                    _serialPort?.Dispose();
                    _cancellationTokenSource?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}