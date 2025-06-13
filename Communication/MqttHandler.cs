using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using UniMixerServer.Configuration;
using UniMixerServer.Models;

namespace UniMixerServer.Communication {
    public class MqttHandler : ICommunicationHandler, IDisposable {
        private readonly ILogger<MqttHandler> _logger;
        private readonly MqttConfig _config;
        private IManagedMqttClient? _mqttClient;
        private bool _disposed = false;

        public string Name => "MQTT";
        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        public event EventHandler<StatusUpdateReceivedEventArgs>? StatusUpdateReceived;
        public event EventHandler<StatusRequestReceivedEventArgs>? StatusRequestReceived;
        public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        public MqttHandler(ILogger<MqttHandler> logger, MqttConfig config) {
            _logger = logger;
            _config = config;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default) {
            try {
                _logger.LogInformation("Starting MQTT handler...");

                var factory = new MqttFactory();
                _mqttClient = factory.CreateManagedMqttClient();

                // Setup event handlers
                _mqttClient.ConnectedAsync += OnConnectedAsync;
                _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
                _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

                // Configure client options
                var clientOptions = new MqttClientOptionsBuilder()
                    .WithClientId(_config.ClientId)
                    .WithTcpServer(_config.BrokerHost, _config.BrokerPort)
                    .WithKeepAlivePeriod(TimeSpan.FromMilliseconds(_config.KeepAliveIntervalMs))
                    .WithCleanSession(true);

                // Add credentials if provided
                if (!string.IsNullOrEmpty(_config.Username)) {
                    clientOptions = clientOptions.WithCredentials(_config.Username, _config.Password);
                }

                // Add TLS if enabled
                if (_config.UseTls) {
                    clientOptions = clientOptions.WithTlsOptions(o => o.UseTls());
                }

                // Configure managed client options
                var managedOptions = new ManagedMqttClientOptionsBuilder()
                    .WithClientOptions(clientOptions.Build())
                    .WithAutoReconnectDelay(TimeSpan.FromMilliseconds(_config.ReconnectDelayMs))
                    .Build();

                // Start the client
                await _mqttClient.StartAsync(managedOptions);

                _logger.LogInformation("MQTT handler started successfully");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to start MQTT handler");
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default) {
            try {
                _logger.LogInformation("Stopping MQTT handler...");

                if (_mqttClient != null) {
                    await _mqttClient.StopAsync();
                    _mqttClient.Dispose();
                    _mqttClient = null;
                }

                _logger.LogInformation("MQTT handler stopped successfully");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error stopping MQTT handler");
                throw;
            }
        }

        public async Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default) {
            if (_mqttClient == null || !IsConnected) {
                _logger.LogWarning("Cannot send status - MQTT client not connected");
                return;
            }

            try {
                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.Topics.StatusTopic)
                    .WithPayload(json)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _mqttClient.EnqueueAsync(message);
                _logger.LogDebug("Status message sent to MQTT topic: {Topic}", _config.Topics.StatusTopic);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending status message via MQTT");
            }
        }

        private async Task OnConnectedAsync(MqttClientConnectedEventArgs args) {
            _logger.LogInformation("MQTT client connected to broker {Host}:{Port}", _config.BrokerHost, _config.BrokerPort);

            // Subscribe to command topics
            await _mqttClient!.SubscribeAsync(_config.Topics.CommandTopic);
            await _mqttClient.SubscribeAsync(_config.Topics.ControlTopic);

            _logger.LogInformation("Subscribed to MQTT topics: {CommandTopic}, {ControlTopic}",
                _config.Topics.CommandTopic, _config.Topics.ControlTopic);

            // Notify connection status change
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                IsConnected = true,
                HandlerName = Name,
                Message = "Connected to MQTT broker"
            });
        }

        private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args) {
            _logger.LogWarning("MQTT client disconnected. Reason: {Reason}", args.Reason);

            // Notify connection status change
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                IsConnected = false,
                HandlerName = Name,
                Message = $"Disconnected from MQTT broker: {args.Reason}"
            });

            await Task.CompletedTask;
        }

        private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args) {
            try {
                var topic = args.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

                _logger.LogDebug("Received MQTT message on topic {Topic}: {Payload}", topic, payload);

                // First try to parse as a status update (new protocol)
                var statusUpdate = TryParseStatusUpdate(payload);
                if (statusUpdate != null) {
                    _logger.LogInformation("StatusUpdate from MQTT: {SessionCount} sessions",
                        statusUpdate.Sessions.Count);
                    StatusUpdateReceived?.Invoke(this, new StatusUpdateReceivedEventArgs {
                        StatusUpdate = statusUpdate,
                        Source = $"MQTT:{topic}",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    return;
                }

                // Try to parse as a status request
                var statusRequest = TryParseStatusRequest(payload);
                if (statusRequest != null) {
                    _logger.LogInformation("StatusRequest from MQTT - triggering status broadcast");

                    StatusRequestReceived?.Invoke(this, new StatusRequestReceivedEventArgs {
                        StatusRequest = statusRequest,
                        Source = $"MQTT:{topic}",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    return;
                }

                _logger.LogWarning("Failed to parse MQTT message from payload: {Payload}", payload);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing MQTT message");
            }

            await Task.CompletedTask;
        }

        private StatusUpdate? TryParseStatusUpdate(string payload) {
            try {
                var update = JsonSerializer.Deserialize<StatusUpdate>(payload, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

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

        private StatusRequest? TryParseStatusRequest(string payload) {
            try {
                var request = JsonSerializer.Deserialize<StatusRequest>(payload, new JsonSerializerOptions {
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

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                    _mqttClient?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}