using System;
using System.Threading;
using System.Threading.Tasks;
using UniMixerServer.Models;

namespace UniMixerServer.Communication
{
    public interface ICommunicationHandler
    {
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
        /// Event fired when a command is received
        /// </summary>
        event EventHandler<CommandReceivedEventArgs>? CommandReceived;

        /// <summary>
        /// Event fired when connection status changes
        /// </summary>
        event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;
    }

    public class CommandReceivedEventArgs : EventArgs
    {
        public AudioCommand Command { get; set; } = new AudioCommand();
        public string Source { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class ConnectionStatusChangedEventArgs : EventArgs
    {
        public bool IsConnected { get; set; }
        public string HandlerName { get; set; } = string.Empty;
        public string? Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}