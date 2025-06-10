using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UniMixerServer.Core
{
    public interface IAudioManager
    {
        /// <summary>
        /// Gets all currently active audio sessions
        /// </summary>
        /// <returns>List of active audio sessions</returns>
        Task<List<AudioSession>> GetAllAudioSessionsAsync();

        /// <summary>
        /// Sets the volume for a specific process
        /// </summary>
        /// <param name="processId">Process ID to control</param>
        /// <param name="volume">Volume level between 0.0 and 1.0</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> SetProcessVolumeAsync(int processId, float volume);

        /// <summary>
        /// Mutes or unmutes a specific process
        /// </summary>
        /// <param name="processId">Process ID to control</param>
        /// <param name="mute">True to mute, false to unmute</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> MuteProcessAsync(int processId, bool mute);

        /// <summary>
        /// Gets the current volume for a specific process
        /// </summary>
        /// <param name="processId">Process ID to query</param>
        /// <returns>Volume level between 0.0 and 1.0, or null if not found</returns>
        Task<float?> GetProcessVolumeAsync(int processId);

        /// <summary>
        /// Gets the current mute state for a specific process
        /// </summary>
        /// <param name="processId">Process ID to query</param>
        /// <returns>True if muted, false if not muted, null if not found</returns>
        Task<bool?> GetProcessMuteStateAsync(int processId);

        /// <summary>
        /// Event fired when audio sessions change
        /// </summary>
        event EventHandler<AudioSessionChangedEventArgs>? AudioSessionChanged;
    }

    public class AudioSessionChangedEventArgs : EventArgs
    {
        public List<AudioSession> Sessions { get; set; } = new List<AudioSession>();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
} 