using System;
using System.Collections.Generic;

namespace UniMixerServer.Models
{
    public class AudioCommand
    {
        public AudioCommandType CommandType { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool Mute { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
    }

    public enum AudioCommandType
    {
        SetVolume,
        Mute,
        Unmute,
        GetStatus,
        GetAllSessions
    }

    public class StatusMessage
    {
        public string DeviceId { get; set; } = Environment.MachineName;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public int ActiveSessionCount { get; set; }
        public List<SessionStatus> Sessions { get; set; } = new List<SessionStatus>();
    }

    public class SessionStatus
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public string State { get; set; } = string.Empty;
    }
}