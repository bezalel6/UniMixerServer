using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Communication.MessageProcessing;
using UniMixerServer.Models;

namespace UniMixerServer.Communication {
    /// <summary>
    /// Base communication handler using O(1) message lookup
    /// </summary>
    public abstract class BaseCommunicationHandler : ICommunicationHandler, IDisposable {
        protected readonly ILogger _logger;
        protected readonly IMessageProcessor _messageProcessor;
        protected bool _disposed = false;

        public abstract string Name { get; }
        public abstract bool IsConnected { get; }

        public event EventHandler<StatusUpdateReceivedEventArgs>? StatusUpdateReceived;
        public event EventHandler<StatusRequestReceivedEventArgs>? StatusRequestReceived;
        public event EventHandler<AssetRequestReceivedEventArgs>? AssetRequestReceived;
        public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        protected BaseCommunicationHandler(ILogger logger, IMessageProcessor messageProcessor) {
            _logger = logger;
            _messageProcessor = messageProcessor;

            // Register message handlers with O(1) lookup
            RegisterMessageHandlers();
        }

        private void RegisterMessageHandlers() {
            _messageProcessor.RegisterHandler(MessageType.STATUS_UPDATE, HandleStatusUpdateAsync);
            _messageProcessor.RegisterHandler(MessageType.STATUS_MESSAGE, HandleStatusUpdateAsync);
            _messageProcessor.RegisterHandler(MessageType.GET_STATUS, HandleStatusRequestAsync);
            _messageProcessor.RegisterHandler(MessageType.GET_ASSETS, HandleAssetRequestAsync);
        }

        public abstract Task StartAsync(CancellationToken cancellationToken = default);
        public abstract Task StopAsync(CancellationToken cancellationToken = default);
        public abstract Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default);
        public abstract Task SendAssetAsync(AssetResponse assetResponse, CancellationToken cancellationToken = default);

        /// <summary>
        /// Processes incoming raw data using the message processor
        /// </summary>
        /// <param name="rawData">Raw data from communication bus</param>
        /// <param name="sourceInfo">Source information for logging</param>
        /// <returns>Task representing the processing</returns>
        protected virtual async Task ProcessIncomingDataAsync(string rawData, string sourceInfo) {
            await _messageProcessor.ProcessAsync(rawData, sourceInfo);
        }

        private async Task HandleStatusUpdateAsync(ParsedMessage message) {
            try {
                // Deserialize the specific message type from the JSON data
                var statusUpdate = message.Data.Deserialize<StatusUpdate>(new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                });

                if (statusUpdate != null) {
                    _logger.LogWarning("🔄 STATUS UPDATE HANDLED: {SessionCount} sessions from {DeviceId} via {Source}",
                        statusUpdate.Sessions.Count, statusUpdate.DeviceId, message.SourceInfo);

                    StatusUpdateReceived?.Invoke(this, new StatusUpdateReceivedEventArgs {
                        StatusUpdate = statusUpdate,
                        Source = message.SourceInfo,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing StatusUpdate from {Source}", message.SourceInfo);
            }

            await Task.CompletedTask;
        }

        private async Task HandleStatusRequestAsync(ParsedMessage message) {
            try {
                // Deserialize the specific message type from the JSON data
                var statusRequest = message.Data.Deserialize<StatusRequest>(new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                });

                if (statusRequest != null) {
                    _logger.LogWarning("📤 SERVER SENDING: STATUS_MESSAGE -> {Source} (responding to {RequestId})",
                        message.SourceInfo, statusRequest.RequestId);

                    // Trigger status request event
                    StatusRequestReceived?.Invoke(this, new StatusRequestReceivedEventArgs {
                        StatusRequest = statusRequest,
                        Source = message.SourceInfo,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
                else {
                    _logger.LogWarning("⚠️ Failed to deserialize status request from {Source}", message.SourceInfo);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "❌ Error handling status request from {Source}", message.SourceInfo);
            }

            await Task.CompletedTask;
        }

        private async Task HandleAssetRequestAsync(ParsedMessage message) {
            try {
                // Deserialize the specific message type from the JSON data
                var assetRequest = message.Data.Deserialize<AssetRequest>(new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                });

                if (assetRequest != null) {
                    _logger.LogWarning("📤 SERVER SENDING: ASSET_RESPONSE -> {Source} (for {ProcessName})",
                        message.SourceInfo, assetRequest.ProcessName);

                    // Trigger asset request event
                    AssetRequestReceived?.Invoke(this, new AssetRequestReceivedEventArgs {
                        AssetRequest = assetRequest,
                        Source = message.SourceInfo,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
                else {
                    _logger.LogWarning("⚠️ Failed to deserialize asset request from {Source}", message.SourceInfo);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "❌ Error handling asset request from {Source}", message.SourceInfo);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Raises a connection status changed event
        /// </summary>
        /// <param name="isConnected">Connection status</param>
        /// <param name="message">Status message</param>
        protected virtual void NotifyConnectionStatusChanged(bool isConnected, string message) {
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs {
                IsConnected = isConnected,
                HandlerName = Name,
                Message = message,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        public virtual void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                    // Derived classes should override this to dispose their resources
                }
                _disposed = true;
            }
        }
    }
}
