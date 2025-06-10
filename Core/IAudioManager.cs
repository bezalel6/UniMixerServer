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
        /// Gets all currently active audio sessions with custom discovery configuration
        /// </summary>
        /// <param name="config">Audio discovery configuration</param>
        /// <returns>List of active audio sessions</returns>
        Task<List<AudioSession>> GetAllAudioSessionsAsync(AudioDiscoveryConfig? config);

        /// <summary>
        /// Sets the volume for a specific process
        /// </summary>
        /// <param name="processId">Process ID to control</param>
        /// <param name="volume">Volume level between 0.0 and 1.0</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> SetProcessVolumeAsync(int processId, float volume);

        /// <summary>
        /// Sets the volume for a specific process by name
        /// </summary>
        /// <param name="processName">Process name to control</param>
        /// <param name="volume">Volume level between 0.0 and 1.0</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> SetProcessVolumeByNameAsync(string processName, float volume);

        /// <summary>
        /// Mutes or unmutes a specific process
        /// </summary>
        /// <param name="processId">Process ID to control</param>
        /// <param name="mute">True to mute, false to unmute</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> MuteProcessAsync(int processId, bool mute);

        /// <summary>
        /// Mutes or unmutes a specific process by name
        /// </summary>
        /// <param name="processName">Process name to control</param>
        /// <param name="mute">True to mute, false to unmute</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> MuteProcessByNameAsync(string processName, bool mute);

        /// <summary>
        /// Gets the current volume for a specific process
        /// </summary>
        /// <param name="processId">Process ID to query</param>
        /// <returns>Volume level between 0.0 and 1.0, or null if not found</returns>
        Task<float?> GetProcessVolumeAsync(int processId);

        /// <summary>
        /// Gets the current volume for a specific process by name
        /// </summary>
        /// <param name="processName">Process name to query</param>
        /// <returns>Volume level between 0.0 and 1.0, or null if not found</returns>
        Task<float?> GetProcessVolumeByNameAsync(string processName);

        /// <summary>
        /// Gets the current mute state for a specific process
        /// </summary>
        /// <param name="processId">Process ID to query</param>
        /// <returns>True if muted, false if not muted, null if not found</returns>
        Task<bool?> GetProcessMuteStateAsync(int processId);

        /// <summary>
        /// Gets the current mute state for a specific process by name
        /// </summary>
        /// <param name="processName">Process name to query</param>
        /// <returns>True if muted, false if not muted, null if not found</returns>
        Task<bool?> GetProcessMuteStateByNameAsync(string processName);

        /// <summary>
        /// Gets information about the default audio device
        /// </summary>
        /// <param name="dataFlow">Data flow direction (Render/Capture)</param>
        /// <param name="deviceRole">Device role (Console/Multimedia/Communications)</param>
        /// <returns>Default audio device information, or null if not found</returns>
        Task<DefaultAudioDeviceInfo?> GetDefaultAudioDeviceAsync(AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console);

        /// <summary>
        /// Sets the volume for the default audio device
        /// </summary>
        /// <param name="volume">Volume level between 0.0 and 1.0</param>
        /// <param name="dataFlow">Data flow direction (Render/Capture)</param>
        /// <param name="deviceRole">Device role (Console/Multimedia/Communications)</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> SetDefaultDeviceVolumeAsync(float volume, AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console);

        /// <summary>
        /// Mutes or unmutes the default audio device
        /// </summary>
        /// <param name="mute">True to mute, false to unmute</param>
        /// <param name="dataFlow">Data flow direction (Render/Capture)</param>
        /// <param name="deviceRole">Device role (Console/Multimedia/Communications)</param>
        /// <returns>True if successful, false otherwise</returns>
        Task<bool> MuteDefaultDeviceAsync(bool mute, AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console);

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

    public class DefaultAudioDeviceInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public AudioDataFlow DataFlow { get; set; }
        public AudioDeviceRole DeviceRole { get; set; }
    }
}