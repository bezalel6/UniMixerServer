using System;
using System.Threading;
using System.Threading.Tasks;
using UniMixerServer.Models;

namespace UniMixerServer.Communication {
    public interface ICommunicationHandler {
        /// <summary>
        /// Gets the name of this communication handler
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Indicates if the handler is currently connected/active
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Starts the communication handler
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the start operation</returns>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops the communication handler
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the stop operation</returns>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a status message through this communication channel
        /// </summary>
        /// <param name="status">Status message to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        Task SendStatusAsync(StatusMessage status, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends an asset response through this communication channel
        /// </summary>
        /// <param name="assetResponse">Asset response to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        Task SendAssetAsync(AssetResponse assetResponse, CancellationToken cancellationToken = default);

        /// <summary>
        /// Event fired when a status update is received
        /// </summary>
        event EventHandler<StatusUpdateReceivedEventArgs>? StatusUpdateReceived;

        /// <summary>
        /// Event fired when a status request is received
        /// </summary>
        event EventHandler<StatusRequestReceivedEventArgs>? StatusRequestReceived;

        /// <summary>
        /// Event fired when an asset request is received
        /// </summary>
        event EventHandler<AssetRequestReceivedEventArgs>? AssetRequestReceived;

        /// <summary>
        /// Event fired when connection status changes
        /// </summary>
        event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;
    }

    public class StatusUpdateReceivedEventArgs : EventArgs {
        public StatusUpdate StatusUpdate { get; set; } = new StatusUpdate();
        public string Source { get; set; } = string.Empty;
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public class StatusRequestReceivedEventArgs : EventArgs {
        public StatusRequest StatusRequest { get; set; } = new StatusRequest();
        public string Source { get; set; } = string.Empty;
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public class AssetRequestReceivedEventArgs : EventArgs {
        public AssetRequest AssetRequest { get; set; } = new AssetRequest();
        public string Source { get; set; } = string.Empty;
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public class ConnectionStatusChangedEventArgs : EventArgs {
        public bool IsConnected { get; set; }
        public string HandlerName { get; set; } = string.Empty;
        public string? Message { get; set; }
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
