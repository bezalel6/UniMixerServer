using System;
using System.Collections.Generic;

namespace UniMixerServer.Models {
    // New simplified protocol models
    public class StatusUpdate {
        public string MessageType { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public long Timestamp { get; set; }  // Milliseconds since epoch
        public List<SessionUpdate> Sessions { get; set; } = new List<SessionUpdate>();
        public DefaultAudioDevice? DefaultDevice { get; set; }
    }

    public class StatusRequest {
        public string MessageType { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }

    public class SessionUpdate {
        public string ProcessName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public string State { get; set; } = string.Empty;
    }



    public class StatusMessage {
        public string DeviceId { get; set; } = Environment.MachineName;
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public int ActiveSessionCount { get; set; }
        public List<SessionStatus> Sessions { get; set; } = new List<SessionStatus>();
        public DefaultAudioDevice? DefaultDevice { get; set; }
        public string Reason { get; set; } = StatusBroadcastReason.Unknown.ToString();
        public string? OriginatingRequestId { get; set; }
        public string? OriginatingDeviceId { get; set; }
    }

    public enum StatusBroadcastReason {
        Unknown,
        ServiceStartup,
        PeriodicUpdate,
        SessionChange,
        StatusRequest,
        UpdateResponse
    }

    public class SessionStatus {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public string State { get; set; } = string.Empty;
    }

    public class DefaultAudioDevice {
        public string FriendlyName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public string DataFlow { get; set; } = string.Empty; // "Render" or "Capture"
        public string DeviceRole { get; set; } = string.Empty; // "Console", "Multimedia", "Communications"
    }
}