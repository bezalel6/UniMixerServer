using System;
using System.Collections.Generic;

namespace UniMixerServer.Models {
    /// <summary>
    /// Factory class for creating messages with proper message types
    /// </summary>
    public static class MessageFactory {
        /// <summary>
        /// Creates a StatusMessage with the correct message type
        /// </summary>
        public static StatusMessage CreateStatusMessage(string deviceId = "", StatusBroadcastReason reason = StatusBroadcastReason.Unknown) {
            return new StatusMessage {
                MessageType = "StatusMessage",
                DeviceId = string.IsNullOrEmpty(deviceId) ? Environment.MachineName : deviceId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Reason = reason.ToString()
            };
        }

        /// <summary>
        /// Creates a StatusRequest with the correct message type
        /// </summary>
        public static StatusRequest CreateStatusRequest(string requestId, string deviceId) {
            return new StatusRequest {
                MessageType = "GetStatus",
                RequestId = requestId,
                DeviceId = deviceId
            };
        }

        /// <summary>
        /// Creates an AssetRequest with the correct message type
        /// </summary>
        public static AssetRequest CreateAssetRequest(string requestId, string deviceId, string processName) {
            return new AssetRequest {
                MessageType = "GetAssets",
                RequestId = requestId,
                DeviceId = deviceId,
                ProcessName = processName
            };
        }

        /// <summary>
        /// Creates an AssetResponse with the correct message type
        /// </summary>
        public static AssetResponse CreateAssetResponse(string requestId, string deviceId, string processName) {
            return new AssetResponse {
                MessageType = "AssetResponse",
                RequestId = requestId,
                DeviceId = deviceId,
                ProcessName = processName
            };
        }

        /// <summary>
        /// Creates a StatusUpdate with the correct message type
        /// </summary>
        public static StatusUpdate CreateStatusUpdate(string requestId, string deviceId) {
            return new StatusUpdate {
                MessageType = "StatusUpdate",
                RequestId = requestId,
                DeviceId = deviceId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        /// <summary>
        /// Creates a SessionUpdate with the correct message type
        /// </summary>
        public static SessionUpdate CreateSessionUpdate(string processName, float volume, bool isMuted, string state) {
            return new SessionUpdate {
                MessageType = "SessionUpdate",
                ProcessName = processName,
                Volume = volume,
                IsMuted = isMuted,
                State = state
            };
        }
    }
}
