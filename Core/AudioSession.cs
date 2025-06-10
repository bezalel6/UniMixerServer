using System;

namespace UniMixerServer.Core
{
    public class AudioSession
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public int SessionState { get; set; }
        public string IconPath { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public string UniqueId => $"{ProcessId}_{ProcessName}";

        public override string ToString()
        {
            return $"{ProcessName} (PID: {ProcessId}) - Volume: {Volume:P0}, Muted: {IsMuted}";
        }
    }

    public enum AudioSessionState
    {
        AudioSessionStateInactive = 0,
        AudioSessionStateActive = 1,
        AudioSessionStateExpired = 2
    }
}