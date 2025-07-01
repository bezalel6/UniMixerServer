using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UniMixerServer.Models;

namespace UniMixerServer.Communication.MessageProcessing {
    /// <summary>
    /// Base interface for parsed messages that contain a message type
    /// </summary>
    public interface IParsedMessage {
        MessageType MessageType { get; }
    }

    /// <summary>
    /// Generic parsed message that holds the raw JSON data
    /// </summary>
    public class ParsedMessage : IParsedMessage {
        public MessageType MessageType { get; set; } = MessageType.INVALID;
        public JsonElement Data { get; set; }
        public string SourceInfo { get; set; } = string.Empty;
    }

    /// <summary>
    /// Handler for a specific message type
    /// </summary>
    /// <param name="message">The parsed message</param>
    /// <returns>Task representing the processing</returns>
    public delegate Task MessageHandler(ParsedMessage message);

    /// <summary>
    /// Message processor that handles O(1) lookup and dispatch
    /// </summary>
    public interface IMessageProcessor : IDisposable {
        /// <summary>
        /// Registers a handler for a specific message type
        /// </summary>
        /// <param name="messageType">The message type to handle</param>
        /// <param name="handler">The handler function</param>
        void RegisterHandler(MessageType messageType, MessageHandler handler);

        /// <summary>
        /// Processes raw data through bus-specific parsing and message dispatch
        /// </summary>
        /// <param name="rawData">Raw data from communication bus</param>
        /// <param name="sourceInfo">Source information for logging</param>
        /// <returns>Task representing the processing</returns>
        Task ProcessAsync(string rawData, string sourceInfo);
    }
}
