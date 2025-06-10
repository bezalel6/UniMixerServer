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

namespace UniMixerServer.Communication
{
    public class MqttHandler : ICommunicationHandler, IDisposable
    {
        private readonly ILogger<MqttHandler> _logger;
        private readonly MqttConfig _config;
        private IManagedMqttClient? _mqttClient;
        private bool _disposed = false;

        public string Name => "MQTT";
        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        public event EventHandler<CommandReceivedEventArgs>? CommandReceived;
        public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        public MqttHandler(ILogger<MqttHandler> logger, MqttConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
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
                if (!string.IsNullOrEmpty(_config.Username))
                {
                    clientOptions = clientOptions.WithCredentials(_config.Username, _config.Password);
                }

                // Add TLS if enabled
                if (_config.UseTls)
                {
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start MQTT handler");
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Stopping MQTT handler...");

                if (_mqttClient != null)
                {
                    await _mqttClient.StopAsync();
                    _mqttClient.Dispose();
                    _mqttClient = null;
                }

                _logger.LogInformation("MQTT handler stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping MQTT handler");
                throw;
            }
        }

        public async Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default)
        {
            if (_mqttClient == null || !IsConnected)
            {
                _logger.LogWarning("Cannot send status - MQTT client not connected");
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions 
                { 
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending status message via MQTT");
            }
        }

        public async Task SendCommandResultAsync(CommandResult result, CancellationToken cancellationToken = default)
        {
            if (_mqttClient == null || !IsConnected)
            {
                _logger.LogWarning("Cannot send command result - MQTT client not connected");
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions 
                { 
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                });

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.Topics.ResponseTopic)
                    .WithPayload(json)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _mqttClient.EnqueueAsync(message);
                _logger.LogDebug("Command result sent to MQTT topic: {Topic}", _config.Topics.ResponseTopic);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending command result via MQTT");
            }
        }

        private async Task OnConnectedAsync(MqttClientConnectedEventArgs args)
        {
            _logger.LogInformation("MQTT client connected to broker {Host}:{Port}", _config.BrokerHost, _config.BrokerPort);

            // Subscribe to command topics
            await _mqttClient!.SubscribeAsync(_config.Topics.CommandTopic);
            await _mqttClient.SubscribeAsync(_config.Topics.ControlTopic);

            _logger.LogInformation("Subscribed to MQTT topics: {CommandTopic}, {ControlTopic}", 
                _config.Topics.CommandTopic, _config.Topics.ControlTopic);

            // Notify connection status change
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
            {
                IsConnected = true,
                HandlerName = Name,
                Message = "Connected to MQTT broker"
            });
        }

        private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            _logger.LogWarning("MQTT client disconnected. Reason: {Reason}", args.Reason);

            // Notify connection status change
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
            {
                IsConnected = false,
                HandlerName = Name,
                Message = $"Disconnected from MQTT broker: {args.Reason}"
            });

            await Task.CompletedTask;
        }

        private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var topic = args.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

                _logger.LogDebug("Received MQTT message on topic {Topic}: {Payload}", topic, payload);

                // Parse the command from JSON
                var command = JsonSerializer.Deserialize<AudioCommand>(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                if (command != null)
                {
                    // Fire the command received event
                    CommandReceived?.Invoke(this, new CommandReceivedEventArgs
                    {
                        Command = command,
                        Source = $"MQTT:{topic}",
                        Timestamp = DateTime.UtcNow
                    });
                }
                else
                {
                    _logger.LogWarning("Failed to parse MQTT command from payload: {Payload}", payload);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing MQTT message");
            }

            await Task.CompletedTask;
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
                    _mqttClient?.Dispose();
                }
                _disposed = true;
            }
        }
    }
} 