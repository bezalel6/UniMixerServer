namespace UniMixerServer.Models {
    /// <summary>
    /// Enum mapping for message types used in the communication protocol.
    /// These values should match the communicating partner's definitions.
    /// </summary>
    public enum MessageType {
        /// <summary>
        /// Invalid or unknown message type
        /// </summary>
        INVALID = 0,

        /// <summary>
        /// Status update message containing session information
        /// Maps to: "StatusUpdate"
        /// </summary>
        STATUS_UPDATE = 1,

        /// <summary>
        /// Status broadcast message
        /// Maps to: "StatusMessage" 
        /// </summary>
        STATUS_MESSAGE = 2,

        /// <summary>
        /// Request for device status
        /// Maps to: "GetStatus"
        /// </summary>
        GET_STATUS = 3,

        /// <summary>
        /// Request for asset data (e.g., process icons)
        /// Maps to: "GetAssets"
        /// </summary>
        GET_ASSETS = 4,

        /// <summary>
        /// Response containing asset data
        /// Maps to: "AssetResponse"
        /// </summary>
        ASSET_RESPONSE = 5,

        /// <summary>
        /// Individual session update (used within StatusUpdate)
        /// Maps to: "SessionUpdate"
        /// </summary>
        SESSION_UPDATE = 6
    }

    /// <summary>
    /// Extension methods for MessageType enum
    /// </summary>
    public static class MessageTypeExtensions {
        /// <summary>
        /// Convert MessageType enum to its corresponding string value
        /// </summary>
        /// <param name="messageType">The message type enum</param>
        /// <returns>String representation used in JSON messages</returns>
        public static string ToMessageString(this MessageType messageType) {
            return messageType switch {
                MessageType.STATUS_UPDATE => "StatusUpdate",
                MessageType.STATUS_MESSAGE => "StatusMessage",
                MessageType.GET_STATUS => "GetStatus",
                MessageType.GET_ASSETS => "GetAssets",
                MessageType.ASSET_RESPONSE => "AssetResponse",
                MessageType.SESSION_UPDATE => "SessionUpdate",
                MessageType.INVALID => "Invalid",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Parse a string message type to its corresponding enum value
        /// </summary>
        /// <param name="messageTypeString">String message type from JSON</param>
        /// <returns>Corresponding MessageType enum value</returns>
        public static MessageType FromMessageString(string messageTypeString) {
            return messageTypeString switch {
                "StatusUpdate" => MessageType.STATUS_UPDATE,
                "StatusMessage" => MessageType.STATUS_MESSAGE,
                "GetStatus" => MessageType.GET_STATUS,
                "GetAssets" => MessageType.GET_ASSETS,
                "AssetResponse" => MessageType.ASSET_RESPONSE,
                "SessionUpdate" => MessageType.SESSION_UPDATE,
                _ => MessageType.INVALID
            };
        }
    }
}
