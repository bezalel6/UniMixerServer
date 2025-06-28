using System;
using System.Collections.Generic;

namespace UniMixerServer.Models {
    // New simplified protocol models
    public class StatusUpdate {
        public string MessageType { get; set; } = "StatusUpdate";
        public string RequestId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public long Timestamp { get; set; }  // Milliseconds since epoch
        public List<SessionUpdate> Sessions { get; set; } = new List<SessionUpdate>();
        public DefaultAudioDevice? DefaultDevice { get; set; }
    }

    public class StatusRequest {
        public string MessageType { get; set; } = "GetStatus";
        public string RequestId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }
    public class AssetRequest {
        public string MessageType { get; set; } = "GetAssets";
        public string RequestId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
    }

    public class SessionUpdate {
        public string MessageType { get; set; } = "SessionUpdate";
        public string ProcessName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public string State { get; set; } = string.Empty;
    }



    public class StatusMessage {
        public string MessageType { get; set; } = "StatusMessage";
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

    // Asset and metadata models
    public class AssetResponse {
        public string MessageType { get; set; } = "AssetResponse";
        public string RequestId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public LogoMetadata? Metadata { get; set; }
        public byte[]? AssetData { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class LogoMetadata {
        public string ProcessName { get; set; } = string.Empty;
        public string Patterns { get; set; } = string.Empty; // Regex patterns (comma-separated)
        public uint FileSize { get; set; }
        public ushort Width { get; set; }
        public ushort Height { get; set; }
        public string Format { get; set; } = string.Empty; // "lvgl_bin", "lvgl_indexed", etc.
        public string Checksum { get; set; } = string.Empty; // MD5 hex string
        public ulong CreatedTimestamp { get; set; }
        public ulong ModifiedTimestamp { get; set; }
        public UserFlags UserFlags { get; set; } = new UserFlags();
        public byte MatchConfidence { get; set; } // 0-100 confidence score
        public byte Version { get; set; } = 1; // Metadata format version
    }

    public class UserFlags {
        public bool Incorrect { get; set; }        // User flagged as incorrect match
        public bool Verified { get; set; }         // User verified as correct match
        public bool Custom { get; set; }           // User uploaded custom logo
        public bool AutoDetected { get; set; }     // Automatically detected/downloaded
        public bool ManualAssignment { get; set; } // User manually assigned this logo
    }

    public class LogoFormat {
        public string Format { get; set; } = "png"; // "png", "lvgl_bin", "lvgl_indexed", etc.
        public int Width { get; set; } = 32;
        public int Height { get; set; } = 32;
    }
}
