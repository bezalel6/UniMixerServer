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

namespace UniMixerServer.Communication
{
    public class SerialHandler : ICommunicationHandler, IDisposable
    {
        private const string MESSAGE_START_DELIMITER = "<MSG>";
        private const string MESSAGE_END_DELIMITER = "</MSG>";
        
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
                _logger.LogDebug("Creating status message for {SessionCount} sessions", status.Sessions.Count);
                
                // Create a clean JSON structure that matches what the ESP32 expects
                var sessionsList = new List<object>();
                int sessionIndex = 0;
                foreach (var session in status.Sessions.Take(5))
                {
                    _logger.LogDebug("Processing session {Index}: PID={ProcessId}, Name='{ProcessName}', Volume={Volume}, Muted={IsMuted}, State='{State}'", 
                        sessionIndex, session.ProcessId, session.ProcessName, session.Volume, session.IsMuted, session.State);
                    
                    var sessionDict = new Dictionary<string, object>
                    {
                        ["processName"] = session.ProcessName ?? string.Empty,
                        ["processId"] = session.ProcessId,
                        ["volume"] = session.Volume,
                        ["isMuted"] = session.IsMuted,
                        ["state"] = session.State ?? string.Empty
                    };
                    
                    sessionsList.Add(sessionDict);
                    sessionIndex++;
                }

                var statusData = new Dictionary<string, object>
                {
                    ["sessions"] = sessionsList
                };

                var json = JsonSerializer.Serialize(statusData, new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                _logger.LogDebug("Serialized JSON ({Length} chars): {Json}", json.Length, json);

                var message = $"{MESSAGE_START_DELIMITER}{json}{MESSAGE_END_DELIMITER}\n";
                
                _logger.LogDebug("Final message ({Length} chars): {Message}", message.Length, message.TrimEnd());
                _logger.LogDebug("Message bytes (first 100): {Bytes}", string.Join(" ", System.Text.Encoding.UTF8.GetBytes(message).Take(100).Select(b => $"{b:X2}")));
                await Task.Run(() => _serialPort!.Write(message), cancellationToken);
                _logger.LogDebug("Status message sent via serial port successfully");
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

                var message = $"{MESSAGE_START_DELIMITER}{json}{MESSAGE_END_DELIMITER}\n";
                
                _logger.LogDebug("Sending command result via serial port: {Message}", message.TrimEnd());
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
                        _logger.LogDebug("Received raw serial data: {Data}", data);
                        buffer.Append(data);

                        // Process complete messages (wrapped with delimiters)
                        var content = buffer.ToString();
                        
                        // Extract messages between delimiters
                        var messages = ExtractWrappedMessages(content);
                        
                        // Process all complete messages
                        foreach (var message in messages.CompleteMessages)
                        {
                            await ProcessSerialMessage(message);
                        }

                        // Keep the remaining content in buffer
                        buffer.Clear();
                        buffer.Append(messages.RemainingContent);
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

        private (List<string> CompleteMessages, string RemainingContent) ExtractWrappedMessages(string content)
        {
            var completeMessages = new List<string>();
            var remainingContent = content;

            while (true)
            {
                var startIndex = remainingContent.IndexOf(MESSAGE_START_DELIMITER);
                if (startIndex == -1)
                    break;

                var endIndex = remainingContent.IndexOf(MESSAGE_END_DELIMITER, startIndex + MESSAGE_START_DELIMITER.Length);
                if (endIndex == -1)
                    break;

                // Extract the message content between delimiters
                var messageStart = startIndex + MESSAGE_START_DELIMITER.Length;
                var messageLength = endIndex - messageStart;
                var message = remainingContent.Substring(messageStart, messageLength);
                
                if (!string.IsNullOrWhiteSpace(message))
                {
                    completeMessages.Add(message);
                    _logger.LogDebug("Extracted wrapped message: {Message}", message);
                }

                // Remove the processed message from remaining content
                remainingContent = remainingContent.Substring(endIndex + MESSAGE_END_DELIMITER.Length);
            }

            return (completeMessages, remainingContent);
        }

        private async Task ProcessSerialMessage(string message)
        {
            try
            {
                _logger.LogDebug("Received serial message: {Message}", message);

                // All messages from ESP32 are commands - no prefix needed
                var command = JsonSerializer.Deserialize<AudioCommand>(message, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters = { new JsonStringEnumConverter() }
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