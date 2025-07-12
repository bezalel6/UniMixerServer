namespace UniMixerServer.Models {
    /// <summary>
    /// Message type constants used throughout the communication protocol
    /// </summary>
    public static class MessageTypes {
        /// <summary>
        /// Status update message containing session information
        /// </summary>
        public const string STATUS_UPDATE = "STATUS_UPDATE";

        /// <summary>
        /// Status broadcast message
        /// </summary>
        public const string STATUS_MESSAGE = "STATUS_MESSAGE";

        /// <summary>
        /// Request for device status
        /// </summary>
        public const string GET_STATUS = "GET_STATUS";

        /// <summary>
        /// Request for asset data (e.g., process icons)
        /// </summary>
        public const string GET_ASSETS = "GET_ASSETS";

        /// <summary>
        /// Response containing asset data
        /// </summary>
        public const string ASSET_RESPONSE = "ASSET_RESPONSE";

        /// <summary>
        /// Individual session update (used within StatusUpdate)
        /// </summary>
        public const string SESSION_UPDATE = "SESSION_UPDATE";
    }
} 