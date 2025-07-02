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
using UniMixerServer.Communication.MessageProcessing;
using UniMixerServer.Services;

namespace UniMixerServer.Communication {
    /// <summary>
    /// MQTT handler using O(1) message processing
    /// </summary>
    public class MqttHandler : BaseCommunicationHandler {
        private readonly MqttConfig _config;
        private IManagedMqttClient? _mqttClient;

        public override string Name => "MQTT";
        public override bool IsConnected => _mqttClient?.IsConnected ?? false;

        public MqttHandler(ILogger<MqttHandler> logger, MqttConfig config, JsonMessageProcessor messageProcessor)
            : base(logger, messageProcessor) {
            _config = config;
        }

        public override async Task StartAsync(CancellationToken cancellationToken = default) {
            try {
                _logger.LogInformation("Starting MQTT handler...");

                var factory = new MqttFactory();
                _mqttClient = factory.CreateManagedMqttClient();

                _mqttClient.ConnectedAsync += OnConnectedAsync;
                _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
                _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

                var clientOptions = new MqttClientOptionsBuilder()
                    .WithClientId(_config.ClientId)
                    .WithTcpServer(_config.BrokerHost, _config.BrokerPort)
                    .WithKeepAlivePeriod(TimeSpan.FromMilliseconds(_config.KeepAliveIntervalMs))
                    .WithCleanSession(true);

                if (!string.IsNullOrEmpty(_config.Username)) {
                    clientOptions = clientOptions.WithCredentials(_config.Username, _config.Password);
                }

                if (_config.UseTls) {
                    clientOptions = clientOptions.WithTlsOptions(o => o.UseTls());
                }

                var managedOptions = new ManagedMqttClientOptionsBuilder()
                    .WithClientOptions(clientOptions.Build())
                    .WithAutoReconnectDelay(TimeSpan.FromMilliseconds(_config.ReconnectDelayMs))
                    .Build();

                await _mqttClient.StartAsync(managedOptions);

                _logger.LogInformation("MQTT handler started successfully");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to start MQTT handler");
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken = default) {
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

        public override async Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default) {
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

                // Log outgoing data
                OutgoingDataLogger.LogOutgoingData(json, $"MQTT:{_config.Topics.StatusTopic}");

                await _mqttClient.EnqueueAsync(message);
                _logger.LogDebug("Status message sent to MQTT topic: {Topic}", _config.Topics.StatusTopic);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending status message via MQTT");
            }
        }

        public override async Task SendAssetAsync(AssetResponse assetResponse, CancellationToken cancellationToken = default) {
            if (_mqttClient == null || !IsConnected) {
                _logger.LogWarning("Cannot send asset - MQTT client not connected");
                return;
            }

            try {
                // For MQTT communication, we'll send asset data as base64 encoded JSON
                // Create a serializable version with base64 encoded asset data
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

                // Use assets topic (could be configured in config later)
                var assetsTopic = _config.Topics.StatusTopic.Replace("/status", "/assets");
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(assetsTopic)
                    .WithPayload(json)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag(false)
                    .Build();

                // Log outgoing data
                OutgoingDataLogger.LogOutgoingData(json, $"MQTT:{assetsTopic}");

                await _mqttClient.EnqueueAsync(message);
                _logger.LogDebug("Asset response sent to MQTT topic: {Topic} for process: {ProcessName}",
                    assetsTopic, assetResponse.ProcessName);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error sending asset response via MQTT");
            }
        }

        private async Task OnConnectedAsync(MqttClientConnectedEventArgs args) {
            _logger.LogInformation("MQTT client connected to broker {Host}:{Port}", _config.BrokerHost, _config.BrokerPort);

            await _mqttClient!.SubscribeAsync(_config.Topics.CommandTopic);
            await _mqttClient.SubscribeAsync(_config.Topics.ControlTopic);

            _logger.LogInformation("Subscribed to MQTT topics: {CommandTopic}, {ControlTopic}",
                _config.Topics.CommandTopic, _config.Topics.ControlTopic);

            NotifyConnectionStatusChanged(true, "Connected to MQTT broker");
        }

        private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args) {
            _logger.LogWarning("MQTT client disconnected. Reason: {Reason}", args.Reason);
            NotifyConnectionStatusChanged(false, $"Disconnected from MQTT broker: {args.Reason}");
            await Task.CompletedTask;
        }

        private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args) {
            try {
                var topic = args.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

                _logger.LogDebug("Received MQTT message on topic {Topic}: {Payload}", topic, payload);

                // Use O(1) message processing
                await ProcessIncomingDataAsync(payload, $"MQTT:{topic}");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing MQTT message");
            }

            await Task.CompletedTask;
        }

        protected override void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                    _mqttClient?.Dispose();
                }
                base.Dispose(disposing);
                _disposed = true;
            }
        }
    }
}
