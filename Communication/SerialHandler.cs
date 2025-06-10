using System;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Configuration;
using UniMixerServer.Models;

namespace UniMixerServer.Communication
{
    public class SerialHandler : ICommunicationHandler, IDisposable
    {
        private readonly ILogger<SerialHandler> _logger;
        private readonly SerialConfig _config;
        private SerialPort? _serialPort;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _readTask;
        private bool _disposed = false;

        public string Name => "Serial";
        public bool IsConnected => _serialPort?.IsOpen ?? false;

        public event EventHandler<CommandReceivedEventArgs>? CommandReceived;
        public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        public SerialHandler(ILogger<SerialHandler> logger, SerialConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting Serial handler on port {Port}...", _config.PortName);

                _serialPort = new SerialPort
                {
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
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
                {
                    IsConnected = true,
                    HandlerName = Name,
                    Message = $"Connected to serial port {_config.PortName}"
                });

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Serial handler");
                
                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
                {
                    IsConnected = false,
                    HandlerName = Name,
                    Message = $"Failed to connect to serial port: {ex.Message}"
                });

                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Stopping Serial handler...");

                // Cancel the read task
                _cancellationTokenSource?.Cancel();

                // Wait for read task to complete
                if (_readTask != null)
                {
                    await _readTask.ConfigureAwait(false);
                }

                // Close the serial port
                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Close();
                }

                _serialPort?.Dispose();
                _serialPort = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.LogInformation("Serial handler stopped successfully");

                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
                {
                    IsConnected = false,
                    HandlerName = Name,
                    Message = "Disconnected from serial port"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Serial handler");
                throw;
            }
        }

        public async Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("Cannot send status - Serial port not connected");
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions 
                { 
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                });

                var message = $"STATUS:{json}\n";
                
                await Task.Run(() => _serialPort!.Write(message), cancellationToken);
                _logger.LogDebug("Status message sent via serial port");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending status message via serial port");
            }
        }

        public async Task SendCommandResultAsync(CommandResult result, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("Cannot send command result - Serial port not connected");
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions 
                { 
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                });

                var message = $"RESULT:{json}\n";
                
                await Task.Run(() => _serialPort!.Write(message), cancellationToken);
                _logger.LogDebug("Command result sent via serial port");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending command result via serial port");
            }
        }

        private async Task ReadSerialDataAsync(CancellationToken cancellationToken)
        {
            var buffer = new StringBuilder();

            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                try
                {
                    if (_serialPort!.BytesToRead > 0)
                    {
                        var data = _serialPort.ReadExisting();
                        buffer.Append(data);

                        // Process complete messages (ended with newline)
                        var content = buffer.ToString();
                        var lines = content.Split('\n');

                        // Process all complete lines
                        for (int i = 0; i < lines.Length - 1; i++)
                        {
                            var line = lines[i].Trim();
                            if (!string.IsNullOrEmpty(line))
                            {
                                await ProcessSerialMessage(line);
                            }
                        }

                        // Keep the last incomplete line in buffer
                        buffer.Clear();
                        if (lines.Length > 0)
                        {
                            buffer.Append(lines[lines.Length - 1]);
                        }
                    }

                    await Task.Delay(10, cancellationToken); // Small delay to prevent busy waiting
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading from serial port");
                    
                    if (_config.EnableAutoReconnect)
                    {
                        await Task.Delay(_config.ReconnectDelayMs, cancellationToken);
                        await TryReconnectAsync();
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private async Task ProcessSerialMessage(string message)
        {
            try
            {
                _logger.LogDebug("Received serial message: {Message}", message);

                // Parse command prefix
                if (message.StartsWith("CMD:", StringComparison.OrdinalIgnoreCase))
                {
                    var json = message.Substring(4);
                    var command = JsonSerializer.Deserialize<AudioCommand>(json, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                    if (command != null)
                    {
                        CommandReceived?.Invoke(this, new CommandReceivedEventArgs
                        {
                            Command = command,
                            Source = "Serial",
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse serial command from message: {Message}", message);
                    }
                }
                else
                {
                    _logger.LogDebug("Ignoring non-command serial message: {Message}", message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing serial message");
            }

            await Task.CompletedTask;
        }

        private async Task TryReconnectAsync()
        {
            try
            {
                _logger.LogInformation("Attempting to reconnect to serial port {Port}...", _config.PortName);

                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Close();
                }

                _serialPort?.Dispose();
                _serialPort = new SerialPort
                {
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
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
                {
                    IsConnected = true,
                    HandlerName = Name,
                    Message = $"Reconnected to serial port {_config.PortName}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconnect to serial port");

                // Notify connection status change
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
                {
                    IsConnected = false,
                    HandlerName = Name,
                    Message = $"Failed to reconnect: {ex.Message}"
                });
            }

            await Task.CompletedTask;
        }

        private static Parity ParseParity(string parity)
        {
            return parity.ToUpperInvariant() switch
            {
                "NONE" => Parity.None,
                "ODD" => Parity.Odd,
                "EVEN" => Parity.Even,
                "MARK" => Parity.Mark,
                "SPACE" => Parity.Space,
                _ => Parity.None
            };
        }

        private static StopBits ParseStopBits(string stopBits)
        {
            return stopBits.ToUpperInvariant() switch
            {
                "NONE" => StopBits.None,
                "ONE" => StopBits.One,
                "TWO" => StopBits.Two,
                "ONEPOINTFIVE" => StopBits.OnePointFive,
                _ => StopBits.One
            };
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
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