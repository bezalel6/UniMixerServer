using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Text.RegularExpressions;

#if WINDOWS
using System.Runtime.Versioning;
#endif

namespace UniMixerServer.Core {
    // Add new enums for configuration
    public enum AudioDataFlow {
        Render = 0,      // Playback/Output devices
        Capture = 1,     // Recording/Input devices  
        All = 2          // Both input and output
    }

    public enum AudioDeviceRole {
        Console = 0,         // Default device for general use
        Multimedia = 1,      // Multimedia applications
        Communications = 2   // Voice communications
    }

    public enum AudioSessionStateFilter {
        All = -1,        // All sessions regardless of state
        Inactive = 0,    // Only inactive sessions
        Active = 1,      // Only active sessions
        Expired = 2      // Only expired sessions
    }

    /// <summary>
    /// Configuration options for audio session discovery and filtering.
    /// </summary>
    /// <example>
    /// Basic usage with exact process name matching:
    /// <code>
    /// var config = new AudioDiscoveryConfig
    /// {
    ///     ProcessNameFilters = new[] { "chrome", "firefox", "spotify" },
    ///     UseRegexFiltering = false
    /// };
    /// var sessions = await audioManager.GetAllAudioSessionsAsync(config);
    /// </code>
    /// 
    /// Advanced usage with regex patterns:
    /// <code>
    /// var config = new AudioDiscoveryConfig
    /// {
    ///     ProcessNameFilters = new[] { "^chrome.*", ".*player.*", "discord" },
    ///     UseRegexFiltering = true,
    ///     StateFilter = AudioSessionStateFilter.Active,
    ///     VerboseLogging = true
    /// };
    /// var sessions = await audioManager.GetAllAudioSessionsAsync(config);
    /// </code>
    /// 
    /// Common regex patterns:
    /// - "^chrome.*" - Matches processes starting with "chrome" (chrome, chrome.exe, chromium, etc.)
    /// - ".*player.*" - Matches processes containing "player" (vlcplayer, mediaplayer, etc.)
    /// - "(?i)discord" - Case-insensitive match for "discord"
    /// - "(spotify|apple music|itunes)" - Matches any of the specified music applications
    /// </example>
    public class AudioDiscoveryConfig {
        public AudioDataFlow DataFlow { get; set; } = AudioDataFlow.Render;
        public AudioDeviceRole DeviceRole { get; set; } = AudioDeviceRole.Console;
        public AudioSessionStateFilter StateFilter { get; set; } = AudioSessionStateFilter.All;
        public bool IncludeAllDevices { get; set; } = false;  // If true, scans ALL audio devices, not just default
        public bool IncludeCaptureDevices { get; set; } = false; // If true, also includes input devices
        public bool VerboseLogging { get; set; } = false;

        /// <summary>
        /// Optional array of process name filters. Can contain either exact string matches or regular expressions.
        /// If null or empty, all sessions are included. If specified, only sessions with process names that match
        /// at least one filter will be included.
        /// Examples:
        /// - ["chrome", "firefox"] - exact matches for Chrome and Firefox
        /// - ["^chrome.*", ".*player.*"] - regex patterns for processes starting with "chrome" or containing "player"
        /// </summary>
        public string[]? ProcessNameFilters { get; set; } = null;

        /// <summary>
        /// If true, treats ProcessNameFilters as regular expressions. If false, treats them as exact string matches.
        /// Default is false for backward compatibility and performance.
        /// </summary>
        public bool UseRegexFiltering { get; set; } = false;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
#endif
    public class AudioManager : IAudioManager, IDisposable {
        private readonly ILogger<AudioManager> _logger;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private readonly bool _enableDetailedLogging;

        public event EventHandler<AudioSessionChangedEventArgs>? AudioSessionChanged;

        public AudioManager(ILogger<AudioManager> logger, bool enableDetailedLogging = false) {
            _logger = logger;
            _enableDetailedLogging = enableDetailedLogging;

            if (_enableDetailedLogging) {
                _logger.LogInformation("AudioManager initialized with detailed logging enabled");
            }
        }

        private void LogDetailed(string message, params object[] args) {
            if (_enableDetailedLogging) {
                _logger.LogInformation(message, args);
            }
        }

        private void LogDetailedWarning(string message, params object[] args) {
            if (_enableDetailedLogging) {
                _logger.LogWarning(message, args);
            }
        }

        private void LogDetailedError(Exception ex, string message, params object[] args) {
            if (_enableDetailedLogging) {
                _logger.LogError(ex, message, args);
            }
        }

        #region COM Interop Declarations

        // COM (Component Object Model) initialization and cleanup functions
        // These are required for any COM-based operations in Windows

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr ptr);

        // CoInitialize: Initializes the COM library for use by the calling thread
        // This MUST be called before any COM operations and MUST be paired with CoUninitialize
        // IntPtr.Zero means use default apartment model (single-threaded apartment)
        [DllImport("ole32.dll")]
        static extern int CoInitialize(IntPtr pvReserved);

        // CoUninitialize: Uninitializes the COM library for the calling thread
        // This MUST be called to clean up COM resources after CoInitialize
        [DllImport("ole32.dll")]
        static extern void CoUninitialize();

        // Core Audio API GUIDs - These are unique identifiers for COM interfaces
        // CLSID_MMDeviceEnumerator: Class ID for the MMDeviceEnumerator COM object
        // This is the main entry point for enumerating audio devices
        static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        // IID_IMMDeviceEnumerator: Interface ID for the IMMDeviceEnumerator interface
        // This interface provides methods to enumerate audio devices
        static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");

        // IID_IAudioSessionManager2: Interface ID for the IAudioSessionManager2 interface
        // This interface provides methods to manage audio sessions
        static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        // IMMDeviceEnumerator: Main interface for enumerating audio devices
        // This is the primary interface we use to discover audio endpoints
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator {
            // EnumAudioEndpoints: Enumerates audio endpoint devices that fit the specified criteria
            // dataFlow: 0=Render (playback), 1=Capture (recording), 2=All
            // stateMask: Bit mask of device states (1=Active, 2=Disabled, 4=NotPresent, 8=Unplugged)
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

            // GetDefaultAudioEndpoint: Gets the default audio endpoint for the specified data flow and role
            // dataFlow: 0=Render, 1=Capture
            // role: 0=Console, 1=Multimedia, 2=Communications
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);

            // GetDevice: Gets an audio endpoint device that is identified by an endpoint ID string
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr device);

            // RegisterEndpointNotificationCallback: Registers a client's notification callback interface
            int RegisterEndpointNotificationCallback(IntPtr client);

            // UnregisterEndpointNotificationCallback: Deletes the registration of a notification interface
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        // IMMDevice: Represents an audio endpoint device
        // This interface provides access to a specific audio device
        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice {
            // Activate: Creates a COM object with the specified interface
            // iid: Interface ID of the requested interface
            // clsCtx: Context for running the executable code (1=CLSCTX_ALL)
            // activationParams: Pointer to activation parameters (usually IntPtr.Zero)
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr interfacePointer);

            // OpenPropertyStore: Opens the property store for the device
            // stgmAccess: Access mode (0=STGM_READ, 1=STGM_WRITE, 2=STGM_READWRITE)
            int OpenPropertyStore(int stgmAccess, out IntPtr properties);

            // GetId: Gets the device ID string for the device
            int GetId(out IntPtr strId);

            // GetState: Gets the current state of the device
            int GetState(out int state);
        }

        // IAudioSessionManager2: Manages audio sessions for an audio device
        // This interface provides access to audio sessions and their controls
        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2 {
            // GetAudioSessionControl: Gets a session control for the specified session
            // groupingParam: Session grouping parameter (usually Guid.Empty)
            // streamFlags: Audio stream flags (0=CrossProcess, 1=CrossProcess, 2=CrossProcess, 4=Loopback)
            int GetAudioSessionControl(ref Guid groupingParam, int streamFlags, out IntPtr sessionControl);

            // GetSimpleAudioVolume: Gets a simple audio volume control for the specified session
            int GetSimpleAudioVolume(ref Guid groupingParam, int streamFlags, out IntPtr audioVolume);

            // GetSessionEnumerator: Gets an enumerator for the audio sessions
            // This is the main method we use to enumerate all audio sessions
            int GetSessionEnumerator(out IntPtr sessionEnum);

            // RegisterSessionNotification: Registers a session notification callback
            int RegisterSessionNotification(IntPtr sessionNotification);

            // UnregisterSessionNotification: Unregisters a session notification callback
            int UnregisterSessionNotification(IntPtr sessionNotification);

            // RegisterDuckNotification: Registers a ducking notification callback
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);

            // UnregisterDuckNotification: Unregisters a ducking notification callback
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        // IAudioSessionEnumerator: Enumerates audio sessions
        // This interface provides access to individual audio sessions
        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator {
            // GetCount: Gets the number of audio sessions
            int GetCount(out int sessionCount);

            // GetSession: Gets the specified audio session
            // sessionNumber: Zero-based index of the session
            int GetSession(int sessionNumber, out IntPtr session);
        }

        // IAudioSessionControl: Controls an audio session
        // This interface provides basic session information and control
        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl {
            // GetState: Gets the current state of the session
            // States: 0=Inactive, 1=Active, 2=Expired
            int GetState(out int state);

            // GetDisplayName: Gets the display name for the session
            int GetDisplayName(out IntPtr name);

            // SetDisplayName: Sets the display name for the session
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

            // GetIconPath: Gets the path to the session's icon
            int GetIconPath(out IntPtr path);

            // SetIconPath: Sets the path to the session's icon
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

            // GetGroupingParam: Gets the session grouping parameter
            int GetGroupingParam(out Guid groupingParam);

            // SetGroupingParam: Sets the session grouping parameter
            int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

            // RegisterAudioSessionNotification: Registers a session notification callback
            int RegisterAudioSessionNotification(IntPtr client);

            // UnregisterAudioSessionNotification: Unregisters a session notification callback
            int UnregisterAudioSessionNotification(IntPtr client);
        }

        // IAudioSessionControl2: Extended audio session control interface
        // This interface inherits from IAudioSessionControl and adds process-specific functionality
        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2 : IAudioSessionControl {
            // Re-declare base interface methods (required for COM interop)
            new int GetState(out int state);
            new int GetDisplayName(out IntPtr name);
            new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            new int GetIconPath(out IntPtr path);
            new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            new int GetGroupingParam(out Guid groupingParam);
            new int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            new int RegisterAudioSessionNotification(IntPtr client);
            new int UnregisterAudioSessionNotification(IntPtr client);

            // Extended methods specific to IAudioSessionControl2

            // GetSessionIdentifier: Gets the session identifier string
            int GetSessionIdentifier(out IntPtr retVal);

            // GetSessionInstanceIdentifier: Gets the session instance identifier string
            int GetSessionInstanceIdentifier(out IntPtr retVal);

            // GetProcessId: Gets the process ID of the session
            // This is the key method we use to identify which process owns the audio session
            int GetProcessId(out int retVal);

            // IsSystemSoundsSession: Determines whether the session is a system sounds session
            int IsSystemSoundsSession();

            // SetDuckingPreference: Sets the ducking preference for the session
            int SetDuckingPreference(bool optOut);
        }

        // ISimpleAudioVolume: Simple audio volume control interface
        // This interface provides basic volume and mute control for audio sessions
        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume {
            // SetMasterVolume: Sets the master volume level for the session
            // level: Volume level from 0.0 (silent) to 1.0 (full volume)
            // eventContext: Context for the volume change event (usually Guid.Empty)
            int SetMasterVolume(float level, ref Guid eventContext);

            // GetMasterVolume: Gets the current master volume level for the session
            int GetMasterVolume(out float level);

            // SetMute: Sets the mute state for the session
            // mute: true to mute, false to unmute
            int SetMute(bool mute, ref Guid eventContext);

            // GetMute: Gets the current mute state for the session
            int GetMute(out bool mute);
        }

        // IMMDeviceCollection: Collection of audio devices
        // This interface provides access to a collection of audio endpoint devices
        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection {
            // GetCount: Gets the number of devices in the collection
            int GetCount(out int deviceCount);

            // Item: Gets the specified device from the collection
            // deviceNumber: Zero-based index of the device
            int Item(int deviceNumber, out IntPtr device);
        }

        // IAudioEndpointVolume: Device endpoint volume control interface
        // This interface controls the master volume of an audio endpoint device
        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume {
            // RegisterControlChangeNotify: Registers a notification callback
            int RegisterControlChangeNotify(IntPtr client);

            // UnregisterControlChangeNotify: Unregisters a notification callback
            int UnregisterControlChangeNotify(IntPtr client);

            // GetChannelCount: Gets the number of channels in the audio stream
            int GetChannelCount(out int channelCount);

            // SetMasterVolumeLevel: Sets the master volume level in decibels
            int SetMasterVolumeLevel(float level, ref Guid eventContext);

            // SetMasterVolumeLevelScalar: Sets the master volume level as a scalar value
            int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

            // GetMasterVolumeLevel: Gets the master volume level in decibels
            int GetMasterVolumeLevel(out float level);

            // GetMasterVolumeLevelScalar: Gets the master volume level as a scalar value
            int GetMasterVolumeLevelScalar(out float level);

            // SetChannelVolumeLevel: Sets the volume level for a specific channel
            int SetChannelVolumeLevel(int channel, float level, ref Guid eventContext);

            // SetChannelVolumeLevelScalar: Sets the volume level for a specific channel as a scalar
            int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext);

            // GetChannelVolumeLevel: Gets the volume level for a specific channel
            int GetChannelVolumeLevel(int channel, out float level);

            // GetChannelVolumeLevelScalar: Gets the volume level for a specific channel as a scalar
            int GetChannelVolumeLevelScalar(int channel, out float level);

            // SetMute: Sets the mute state for the endpoint
            int SetMute(bool mute, ref Guid eventContext);

            // GetMute: Gets the current mute state for the endpoint
            int GetMute(out bool mute);

            // GetVolumeStepInfo: Gets information about volume step increments
            int GetVolumeStepInfo(out int stepCount, out int currentStep);

            // VolumeStepUp: Increases volume by one step
            int VolumeStepUp(ref Guid eventContext);

            // VolumeStepDown: Decreases volume by one step
            int VolumeStepDown(ref Guid eventContext);

            // QueryHardwareSupport: Queries the hardware support capabilities
            int QueryHardwareSupport(out int hardwareSupportMask);

            // GetVolumeRange: Gets the volume range in decibels
            int GetVolumeRange(out float volumeMin, out float volumeMax, out float volumeIncrement);
        }

        // GUID for IAudioEndpointVolume interface
        static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        // IPropertyStore: Property store interface for device properties
        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore {
            // GetCount: Gets the number of properties in the store
            int GetCount(out int propertyCount);

            // GetAt: Gets the property key at the specified index
            int GetAt(int propertyIndex, out PropertyKey key);

            // GetValue: Gets the value of the specified property
            int GetValue(ref PropertyKey key, out PropertyVariant value);

            // SetValue: Sets the value of the specified property
            int SetValue(ref PropertyKey key, ref PropertyVariant value);

            // Commit: Commits changes to the property store
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct PropertyVariant {
            [FieldOffset(0)] public short vt;
            [FieldOffset(8)] public IntPtr pwszVal;
        }

        // Property key for device friendly name
        static readonly PropertyKey PKEY_Device_FriendlyName = new PropertyKey {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        #endregion

        #region Helper Methods

        /// <summary>
        /// Safely converts a COM-allocated string pointer to a .NET string and frees the memory
        /// </summary>
        /// <param name="ptr">Pointer to a COM-allocated Unicode string</param>
        /// <returns>The converted string, or empty string if conversion fails</returns>
        /// <remarks>
        /// COM functions often return strings as pointers to memory they allocate.
        /// This method safely converts these pointers to .NET strings and ensures
        /// the COM-allocated memory is properly freed to prevent memory leaks.
        /// </remarks>
        private static string GetStringFromPointer(IntPtr ptr) {
            if (ptr == IntPtr.Zero)
                return string.Empty;

            try {
                // Convert the COM-allocated Unicode string pointer to a .NET string
                string result = Marshal.PtrToStringUni(ptr);

                // Free the COM-allocated memory to prevent memory leaks
                CoTaskMemFree(ptr);

                return result ?? string.Empty;
            }
            catch {
                // If conversion fails, return empty string rather than throwing
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets the process name for a given process ID, with safety checks and sanitization
        /// </summary>
        /// <param name="processId">The process ID to look up</param>
        /// <returns>The process name, or a fallback string if lookup fails</returns>
        /// <remarks>
        /// This method safely retrieves process names for audio sessions.
        /// It includes several safety measures:
        /// - Handles system sounds session (PID 0)
        /// - Sanitizes process names to remove invalid characters
        /// - Limits name length to prevent buffer issues
        /// - Provides fallback names if process lookup fails
        /// </remarks>
        private static string GetProcessName(int processId) {
            try {
                // Special case: PID 0 represents system sounds
                if (processId == 0) return "System Sounds";

                // Get the process object for the given PID
                var process = Process.GetProcessById(processId);
                var processName = process.ProcessName;

                // Sanitize process name - remove any invalid characters that could cause issues
                if (string.IsNullOrWhiteSpace(processName)) {
                    return $"Unknown Process (PID: {processId})";
                }

                // Remove any control characters or invalid JSON characters that could break serialization
                processName = new string(processName.Where(c => !char.IsControl(c) && c != '"' && c != '\\').ToArray());

                // Limit length to prevent buffer issues and keep responses manageable
                if (processName.Length > 50) {
                    processName = processName.Substring(0, 50);
                }

                // Final fallback if sanitization resulted in empty string
                return string.IsNullOrWhiteSpace(processName) ? $"Process_{processId}" : processName;
            }
            catch {
                // If process lookup fails (e.g., process no longer exists), return fallback name
                return $"Unknown Process (PID: {processId})";
            }
        }

        /// <summary>
        /// Checks if a process name matches any of the configured filters in the discovery config.
        /// </summary>
        /// <param name="processName">The process name to check</param>
        /// <param name="config">The discovery configuration containing the filters</param>
        /// <returns>True if the process name matches the filters or no filters are specified, false otherwise</returns>
        /// <remarks>
        /// This method supports both exact string matches and regular expression patterns based on the
        /// UseRegexFiltering configuration option. If no filters are specified, all process names match.
        /// Filtering is case-insensitive for both exact matches and regex patterns.
        /// </remarks>
        private bool MatchesProcessNameFilter(string processName, AudioDiscoveryConfig config) {
            // If no filters are specified, include all sessions
            if (config.ProcessNameFilters == null || config.ProcessNameFilters.Length == 0) {
                LogDetailed("No process name filters specified - including process: {ProcessName}", processName);
                return true;
            }

            try {
                if (config.UseRegexFiltering) {
                    // Use regular expression matching
                    foreach (var filter in config.ProcessNameFilters) {
                        if (string.IsNullOrWhiteSpace(filter)) continue;

                        try {
                            // Create regex with IgnoreCase option for case-insensitive matching
                            var regex = new Regex(filter, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                            if (regex.IsMatch(processName)) {
                                LogDetailed("Process '{ProcessName}' matched regex filter: '{Filter}'", processName, filter);
                                return true;
                            }
                        }
                        catch (ArgumentException ex) {
                            // Log invalid regex patterns but continue with other filters
                            LogDetailedWarning("Invalid regex pattern '{Filter}': {Error}", filter, ex.Message);
                        }
                    }
                    LogDetailed("Process '{ProcessName}' did not match any regex filters", processName);
                }
                else {
                    // Use contains matching (case-insensitive)
                    foreach (var filter in config.ProcessNameFilters) {
                        if (string.IsNullOrWhiteSpace(filter)) continue;

                        if (processName.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
                            LogDetailed("Process '{ProcessName}' matched contains filter: '{Filter}'", processName, filter);
                            return true;
                        }
                    }
                    LogDetailed("Process '{ProcessName}' did not match any exact filters", processName);
                }

                return false;
            }
            catch (Exception ex) {
                // If filtering fails, log the error and include the session to be safe
                LogDetailedError(ex, "Error checking process name filter for '{ProcessName}' - including session", processName);
                return true;
            }
        }

        #endregion

        #region IAudioManager Implementation

        // Keep the original method for backward compatibility  
        public async Task<List<AudioSession>> GetAllAudioSessionsAsync() {
            // Use default configuration that only includes the default device
            var defaultConfig = new AudioDiscoveryConfig {
                IncludeAllDevices = false,
                IncludeCaptureDevices = false,
                DataFlow = AudioDataFlow.Render,
                DeviceRole = AudioDeviceRole.Console,
                StateFilter = AudioSessionStateFilter.All,
                VerboseLogging = false
            };
            return await GetAllAudioSessionsAsync(defaultConfig);
        }

        /// <summary>
        /// Retrieves all audio sessions from the system with optional filtering and configuration.
        /// </summary>
        /// <param name="config">Optional configuration for session discovery and filtering. If null, uses default settings.</param>
        /// <returns>A list of AudioSession objects representing active audio sessions that match the specified criteria.</returns>
        /// <example>
        /// Example 1 - Filter by exact process names:
        /// <code>
        /// var config = new AudioDiscoveryConfig
        /// {
        ///     ProcessNameFilters = new[] { "chrome", "firefox", "spotify" },
        ///     UseRegexFiltering = false
        /// };
        /// var sessions = await audioManager.GetAllAudioSessionsAsync(config);
        /// </code>
        /// 
        /// Example 2 - Filter using regex patterns:
        /// <code>
        /// var config = new AudioDiscoveryConfig
        /// {
        ///     ProcessNameFilters = new[] { "^chrome.*", ".*player.*" },
        ///     UseRegexFiltering = true,
        ///     StateFilter = AudioSessionStateFilter.Active
        /// };
        /// var sessions = await audioManager.GetAllAudioSessionsAsync(config);
        /// </code>
        /// 
        /// Example 3 - Get all music/media players:
        /// <code>
        /// var config = new AudioDiscoveryConfig
        /// {
        ///     ProcessNameFilters = new[] { "(spotify|vlc|winamp|foobar|itunes|apple music)" },
        ///     UseRegexFiltering = true
        /// };
        /// var sessions = await audioManager.GetAllAudioSessionsAsync(config);
        /// </code>
        /// </example>
        public async Task<List<AudioSession>> GetAllAudioSessionsAsync(AudioDiscoveryConfig? config = null) {
            LogDetailed("Starting GetAllAudioSessionsAsync with custom config");

            // Add aggressive timeout to prevent hanging
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            try {
                return await Task.Run(() => {
                    // Use a timeout wrapper for the internal call
                    return ExecuteWithTimeout(() => {
                        lock (_lock) {
                            LogDetailed("Acquired lock for GetAllAudioSessionsAsync");
                            var result = GetAllAudioSessionsInternal(config ?? new AudioDiscoveryConfig());
                            LogDetailed("Completed GetAllAudioSessionsAsync, returning {SessionCount} sessions", result.Count);
                            return result;
                        }
                    }, TimeSpan.FromSeconds(2));
                }, cts.Token);
            }
            catch (OperationCanceledException) {
                _logger.LogWarning("GetAllAudioSessionsAsync timed out");
                return new List<AudioSession>();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error in GetAllAudioSessionsAsync");
                return new List<AudioSession>();
            }
        }

        private T ExecuteWithTimeout<T>(Func<T> operation, TimeSpan timeout) {
            using var cts = new CancellationTokenSource(timeout);
            var task = Task.Run(operation, cts.Token);

            try {
                return task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) {
                _logger.LogWarning("Operation timed out after {Timeout}ms", timeout.TotalMilliseconds);
                throw;
            }
        }

        public async Task<bool> SetProcessVolumeAsync(int processId, float volume) {
            LogDetailed("Starting SetProcessVolumeAsync for ProcessId: {ProcessId}, Volume: {Volume:P2}", processId, volume);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for SetProcessVolumeAsync");
                    var result = SetProcessVolumeInternal(processId, volume);
                    LogDetailed("Completed SetProcessVolumeAsync with result: {Result}", result);
                    return result;
                }
            });
        }

        public async Task<bool> MuteProcessAsync(int processId, bool mute) {
            LogDetailed("Starting MuteProcessAsync for ProcessId: {ProcessId}, Mute: {Mute}", processId, mute);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for MuteProcessAsync");
                    var result = MuteProcessInternal(processId, mute);
                    LogDetailed("Completed MuteProcessAsync with result: {Result}", result);
                    return result;
                }
            });
        }

        public async Task<float?> GetProcessVolumeAsync(int processId) {
            LogDetailed("Starting GetProcessVolumeAsync for ProcessId: {ProcessId}", processId);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for GetProcessVolumeAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => s.ProcessId == processId);
                    var result = session?.Volume;
                    LogDetailed("Completed GetProcessVolumeAsync with result: {Result}", result?.ToString() ?? "null");
                    return result;
                }
            });
        }

        public async Task<bool?> GetProcessMuteStateAsync(int processId) {
            LogDetailed("Starting GetProcessMuteStateAsync for ProcessId: {ProcessId}", processId);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for GetProcessMuteStateAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => s.ProcessId == processId);
                    var result = session?.IsMuted;
                    LogDetailed("Completed GetProcessMuteStateAsync with result: {Result}", result?.ToString() ?? "null");
                    return result;
                }
            });
        }

        public async Task<bool> SetProcessVolumeByNameAsync(string processName, float volume) {
            LogDetailed("Starting SetProcessVolumeByNameAsync for ProcessName: {ProcessName}, Volume: {Volume:P2}", processName, volume);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for SetProcessVolumeByNameAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

                    if (session == null) {
                        LogDetailedWarning("No session found for ProcessName: {ProcessName}", processName);
                        return false;
                    }

                    var result = SetProcessVolumeInternal(session.ProcessId, volume);
                    LogDetailed("Completed SetProcessVolumeByNameAsync with result: {Result}", result);
                    return result;
                }
            });
        }

        public async Task<bool> MuteProcessByNameAsync(string processName, bool mute) {
            LogDetailed("Starting MuteProcessByNameAsync for ProcessName: {ProcessName}, Mute: {Mute}", processName, mute);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for MuteProcessByNameAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

                    if (session == null) {
                        LogDetailedWarning("No session found for ProcessName: {ProcessName}", processName);
                        return false;
                    }

                    var result = MuteProcessInternal(session.ProcessId, mute);
                    LogDetailed("Completed MuteProcessByNameAsync with result: {Result}", result);
                    return result;
                }
            });
        }

        public async Task<float?> GetProcessVolumeByNameAsync(string processName) {
            LogDetailed("Starting GetProcessVolumeByNameAsync for ProcessName: {ProcessName}", processName);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for GetProcessVolumeByNameAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
                    var result = session?.Volume;
                    LogDetailed("Completed GetProcessVolumeByNameAsync with result: {Result}", result?.ToString() ?? "null");
                    return result;
                }
            });
        }

        public async Task<bool?> GetProcessMuteStateByNameAsync(string processName) {
            LogDetailed("Starting GetProcessMuteStateByNameAsync for ProcessName: {ProcessName}", processName);
            return await Task.Run(() => {
                lock (_lock) {
                    LogDetailed("Acquired lock for GetProcessMuteStateByNameAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
                    var result = session?.IsMuted;
                    LogDetailed("Completed GetProcessMuteStateByNameAsync with result: {Result}", result?.ToString() ?? "null");
                    return result;
                }
            });
        }

        #endregion

        #region Internal Implementation

        private List<AudioSession> GetAllAudioSessionsInternal(AudioDiscoveryConfig? config = null) {
            config ??= new AudioDiscoveryConfig();
            LogDetailed("Starting GetAllAudioSessionsInternal with config: DataFlow={DataFlow}, Role={DeviceRole}, StateFilter={StateFilter}, IncludeAllDevices={IncludeAllDevices}, ProcessNameFilters={ProcessNameFilters}, UseRegexFiltering={UseRegexFiltering}",
                config.DataFlow, config.DeviceRole, config.StateFilter, config.IncludeAllDevices,
                config.ProcessNameFilters != null ? string.Join(", ", config.ProcessNameFilters) : "None", config.UseRegexFiltering);

            var sessions = new List<AudioSession>();
            IntPtr deviceEnumerator = IntPtr.Zero;
            IMMDeviceEnumerator? enumerator = null;

            try {
                // Initialize COM library for this thread
                LogDetailed("Initializing COM");
                var comResult = CoInitialize(IntPtr.Zero);
                LogDetailed("COM initialization result: 0x{ComResult:X8}", comResult);

                // Create the MMDeviceEnumerator COM object with timeout protection
                LogDetailed("Creating MMDeviceEnumerator instance");
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;

                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
                LogDetailed("CoCreateInstance for MMDeviceEnumerator returned HRESULT: 0x{Hr:X8}", hr);

                if (hr != 0 || deviceEnumerator == IntPtr.Zero) {
                    LogDetailedWarning("Failed to create MMDeviceEnumerator, HRESULT: 0x{Hr:X8}", hr);
                    return sessions;
                }

                enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;
                if (enumerator == null) {
                    LogDetailedWarning("Failed to get IMMDeviceEnumerator interface");
                    return sessions;
                }

                LogDetailed("Successfully created IMMDeviceEnumerator interface");

                // Use simplified approach - only get default device to avoid hanging
                if (config.IncludeAllDevices) {
                    LogDetailed("Skipping all devices scan due to timeout risk - using default device only");
                }

                // Get sessions from default device only (fast and reliable)
                var defaultSessions = GetSessionsFromDefaultDevice(enumerator, config);
                sessions.AddRange(defaultSessions);

                LogDetailed("Retrieved {SessionCount} sessions from default device", defaultSessions.Count);
            }
            catch (Exception ex) {
                LogDetailedError(ex, "Error in GetAllAudioSessionsInternal");
                _logger.LogError(ex, "Error getting audio sessions");
            }
            finally {
                // Cleanup COM objects
                try {
                    if (enumerator != null) {
                        Marshal.ReleaseComObject(enumerator);
                        LogDetailed("Released IMMDeviceEnumerator COM object");
                    }
                }
                catch (Exception ex) {
                    LogDetailedError(ex, "Error releasing COM objects");
                }

                // Uninitialize COM library
                try {
                    CoUninitialize();
                    LogDetailed("Uninitializing COM");
                }
                catch (Exception ex) {
                    LogDetailedError(ex, "Error uninitializing COM");
                }
            }

            LogDetailed("GetAllAudioSessionsInternal completed with {SessionCount} sessions", sessions.Count);
            return sessions;
        }

        private bool SetProcessVolumeInternal(int processId, float volume) {
            LogDetailed("Starting SetProcessVolumeInternal for ProcessId: {ProcessId}, Volume: {Volume:P2}", processId, volume);
            try {
                // Clamp volume to valid range (0.0 to 1.0)
                // Windows Core Audio expects volume as a float between 0.0 (silent) and 1.0 (full volume)
                volume = Math.Max(0.0f, Math.Min(1.0f, volume));
                LogDetailed("Clamped volume to: {Volume:P2}", volume);

                // Initialize COM library for this thread
                // Required for all Windows Core Audio API operations
                CoInitialize(IntPtr.Zero);
                LogDetailed("COM initialized for SetProcessVolumeInternal");

                // Create MMDeviceEnumerator COM object (same as in GetAllAudioSessionsInternal)
                IntPtr deviceEnumerator;
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;

                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
                LogDetailed("CoCreateInstance returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;

                // Get the default audio endpoint device
                // Parameters: dataFlow (0=Render/playback), role (0=Console/default)
                IntPtr device;
                hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                LogDetailed("GetDefaultAudioEndpoint returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;

                // Activate the AudioSessionManager2 interface on the device
                // This interface provides access to audio sessions for the device
                IntPtr sessionManager;
                Guid sessionManagerGuid = IID_IAudioSessionManager2;
                hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
                LogDetailed("AudioSessionManager2 activation returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;

                // Get the session enumerator to iterate through all audio sessions
                IntPtr sessionEnumerator;
                hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
                LogDetailed("GetSessionEnumerator returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;

                // Get the total number of audio sessions
                int sessionCount;
                sessionEnum.GetCount(out sessionCount);
                LogDetailed("Found {SessionCount} sessions to search", sessionCount);

                // Iterate through all audio sessions to find the one matching our process ID
                for (int i = 0; i < sessionCount; i++) {
                    IntPtr session;
                    sessionEnum.GetSession(i, out session);

                    // Get the extended session control interface (IAudioSessionControl2)
                    // This interface provides access to process ID and volume control
                    var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;

                    // Get the process ID for this session
                    int sessionProcessId;
                    sessionControl.GetProcessId(out sessionProcessId);
                    LogDetailed("Session {SessionIndex} has ProcessId: {SessionProcessId}", i, sessionProcessId);

                    // Check if this session belongs to the process we're looking for
                    if (sessionProcessId == processId) {
                        LogDetailed("Found matching session for ProcessId: {ProcessId}", processId);

                        // Cast to ISimpleAudioVolume interface to control volume
                        var simpleVolume = sessionControl as ISimpleAudioVolume;
                        if (simpleVolume != null) {
                            // Set the master volume for this session
                            // eventContext is used for notifications (Guid.Empty means no context)
                            Guid eventContext = Guid.Empty;
                            hr = simpleVolume.SetMasterVolume(volume, ref eventContext);
                            LogDetailed("SetMasterVolume returned HRESULT: 0x{Hr:X8}", hr);

                            // Release the session control COM object
                            Marshal.ReleaseComObject(sessionControl);
                            _logger.LogInformation("Set volume for process {ProcessId} to {Volume:P0}", processId, volume);
                            LogDetailed("Successfully set volume for ProcessId: {ProcessId} to {Volume:P2}", processId, volume);
                            return hr == 0;
                        }
                        else {
                            LogDetailedWarning("Failed to get ISimpleAudioVolume interface for ProcessId: {ProcessId}", processId);
                        }
                    }

                    // Release the session control COM object for this session
                    Marshal.ReleaseComObject(sessionControl);
                }

                LogDetailedWarning("No matching session found for ProcessId: {ProcessId}", processId);
                return false;
            }
            catch (Exception ex) {
                LogDetailedError(ex, "Error in SetProcessVolumeInternal for ProcessId: {ProcessId}", processId);
                _logger.LogError(ex, "Error setting volume for process {ProcessId}", processId);
                return false;
            }
            finally {
                // Uninitialize COM library for this thread
                // This MUST be called to clean up COM resources after CoInitialize
                LogDetailed("Uninitializing COM for SetProcessVolumeInternal");
                CoUninitialize();
            }
        }

        private bool MuteProcessInternal(int processId, bool mute) {
            LogDetailed("Starting MuteProcessInternal for ProcessId: {ProcessId}, Mute: {Mute}", processId, mute);
            try {
                // Initialize COM library for this thread
                // Required for all Windows Core Audio API operations
                CoInitialize(IntPtr.Zero);
                LogDetailed("COM initialized for MuteProcessInternal");

                // Create MMDeviceEnumerator COM object (same pattern as SetProcessVolumeInternal)
                IntPtr deviceEnumerator;
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;

                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
                LogDetailed("CoCreateInstance returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;

                // Get the default audio endpoint device (same as volume control)
                IntPtr device;
                hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                LogDetailed("GetDefaultAudioEndpoint returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;

                // Activate the AudioSessionManager2 interface on the device
                IntPtr sessionManager;
                Guid sessionManagerGuid = IID_IAudioSessionManager2;
                hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
                LogDetailed("AudioSessionManager2 activation returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;

                // Get the session enumerator to iterate through all audio sessions
                IntPtr sessionEnumerator;
                hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
                LogDetailed("GetSessionEnumerator returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;

                // Get the total number of audio sessions
                int sessionCount;
                sessionEnum.GetCount(out sessionCount);
                LogDetailed("Found {SessionCount} sessions to search", sessionCount);

                // Iterate through all audio sessions to find the one matching our process ID
                for (int i = 0; i < sessionCount; i++) {
                    IntPtr session;
                    sessionEnum.GetSession(i, out session);

                    // Get the extended session control interface (IAudioSessionControl2)
                    var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;

                    // Get the process ID for this session
                    int sessionProcessId;
                    sessionControl.GetProcessId(out sessionProcessId);
                    LogDetailed("Session {SessionIndex} has ProcessId: {SessionProcessId}", i, sessionProcessId);

                    // Check if this session belongs to the process we're looking for
                    if (sessionProcessId == processId) {
                        LogDetailed("Found matching session for ProcessId: {ProcessId}", processId);

                        // Cast to ISimpleAudioVolume interface to control mute state
                        var simpleVolume = sessionControl as ISimpleAudioVolume;
                        if (simpleVolume != null) {
                            // Set the mute state for this session
                            // eventContext is used for notifications (Guid.Empty means no context)
                            Guid eventContext = Guid.Empty;
                            hr = simpleVolume.SetMute(mute, ref eventContext);
                            LogDetailed("SetMute returned HRESULT: 0x{Hr:X8}", hr);

                            // Release the session control COM object
                            Marshal.ReleaseComObject(sessionControl);
                            _logger.LogInformation("Set mute for process {ProcessId} to {Mute}", processId, mute);
                            LogDetailed("Successfully set mute for ProcessId: {ProcessId} to {Mute}", processId, mute);
                            return hr == 0;
                        }
                        else {
                            LogDetailedWarning("Failed to get ISimpleAudioVolume interface for ProcessId: {ProcessId}", processId);
                        }
                    }

                    // Release the session control COM object for this session
                    Marshal.ReleaseComObject(sessionControl);
                }

                LogDetailedWarning("No matching session found for ProcessId: {ProcessId}", processId);
                return false;
            }
            catch (Exception ex) {
                LogDetailedError(ex, "Error in MuteProcessInternal for ProcessId: {ProcessId}", processId);
                _logger.LogError(ex, "Error setting mute for process {ProcessId}", processId);
                return false;
            }
            finally {
                // Uninitialize COM library for this thread
                // This MUST be called to clean up COM resources after CoInitialize
                LogDetailed("Uninitializing COM for MuteProcessInternal");
                CoUninitialize();
            }
        }

        private List<AudioSession> GetSessionsFromDefaultDevice(IMMDeviceEnumerator enumerator, AudioDiscoveryConfig config) {
            var sessions = new List<AudioSession>();

            // Get the default audio endpoint device for the specified data flow and role
            // This is the device that Windows considers the "default" for audio operations
            LogDetailed("Getting default audio endpoint for DataFlow={DataFlow}, Role={DeviceRole}", config.DataFlow, config.DeviceRole);
            IntPtr device;
            int hr = enumerator.GetDefaultAudioEndpoint((int)config.DataFlow, (int)config.DeviceRole, out device);
            LogDetailed("GetDefaultAudioEndpoint returned HRESULT: 0x{Hr:X8}", hr);

            if (hr != 0) {
                LogDetailedWarning("Failed to get default audio endpoint, HRESULT: 0x{Hr:X8}", hr);
                return sessions;
            }

            // Convert the device COM interface pointer to a .NET interface object
            var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
            LogDetailed("Successfully created IMMDevice interface");

            // Get the actual device name
            string deviceName = GetDeviceName(mmDevice);
            string displayName = $"Default {config.DataFlow} Device: {deviceName}";

            // Get all audio sessions from this default device
            if (mmDevice != null) {
                var deviceSessions = GetSessionsFromDevice(mmDevice, config, displayName);
                sessions.AddRange(deviceSessions);
            }

            // Release the device COM object to prevent memory leaks
            if (mmDevice != null) Marshal.ReleaseComObject(mmDevice);

            return sessions;
        }

        private List<AudioSession> GetSessionsFromAllDevices(IMMDeviceEnumerator enumerator, AudioDiscoveryConfig config) {
            var sessions = new List<AudioSession>();

            // Determine which data flows to enumerate based on configuration
            // This allows scanning for render (playback), capture (recording), or both
            var dataFlows = new List<AudioDataFlow>();
            if (config.DataFlow == AudioDataFlow.All) {
                // If "All" is specified, enumerate both render and capture devices
                dataFlows.Add(AudioDataFlow.Render);
                dataFlows.Add(AudioDataFlow.Capture);
            }
            else {
                // Otherwise, just enumerate the specified data flow
                dataFlows.Add(config.DataFlow);
            }

            // Iterate through each data flow type (render and/or capture)
            foreach (var dataFlow in dataFlows) {
                LogDetailed("Enumerating devices for DataFlow: {DataFlow}", dataFlow);

                // Enumerate all active audio endpoint devices for this data flow
                // The state mask "1" means DEVICE_STATE_ACTIVE (only devices that are currently active)
                IntPtr deviceCollection;
                int hr = enumerator.EnumAudioEndpoints((int)dataFlow, 1, out deviceCollection); // 1 = DEVICE_STATE_ACTIVE
                LogDetailed("EnumAudioEndpoints returned HRESULT: 0x{Hr:X8}", hr);

                if (hr != 0) continue;

                // Convert the device collection COM interface pointer to a .NET interface object
                var collection = Marshal.GetObjectForIUnknown(deviceCollection) as IMMDeviceCollection;
                if (collection == null) continue;

                // Get the total number of devices in this collection
                int deviceCount;
                collection.GetCount(out deviceCount);
                LogDetailed("Found {DeviceCount} active {DataFlow} devices", deviceCount, dataFlow);

                // Iterate through each device in the collection
                for (int i = 0; i < deviceCount; i++) {
                    // Get the device interface pointer for this specific device
                    IntPtr device;
                    hr = collection.Item(i, out device);
                    if (hr != 0) continue;

                    // Convert the device COM interface pointer to a .NET interface object
                    var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
                    if (mmDevice == null) continue;

                    // Get a friendly name for the device for logging purposes
                    string deviceName = GetDeviceName(mmDevice);
                    LogDetailed("Processing device {DeviceIndex}/{DeviceCount}: {DeviceName}", i + 1, deviceCount, deviceName);

                    // Get all audio sessions from this specific device
                    var deviceSessions = GetSessionsFromDevice(mmDevice, config, $"{dataFlow} Device {i}: {deviceName}");
                    sessions.AddRange(deviceSessions);

                    // Release the device COM object to prevent memory leaks
                    Marshal.ReleaseComObject(mmDevice);
                }

                // Release the device collection COM object to prevent memory leaks
                Marshal.ReleaseComObject(collection);
            }

            return sessions;
        }

        private string GetDeviceName(IMMDevice? device) {
            try {
                if (device == null) return "Unknown Device";

                // Get the device ID first
                IntPtr deviceIdPtr;
                int hr = device.GetId(out deviceIdPtr);
                if (hr != 0) return "Unknown Device";

                string deviceId = GetStringFromPointer(deviceIdPtr);
                if (string.IsNullOrEmpty(deviceId)) return "Unknown Device";

                // Try to get a friendly name from the device ID
                // For now, we'll extract a meaningful name from the device ID
                // In a more complete implementation, we would use the property store to get the friendly name

                // Device IDs typically follow this pattern:
                // {0.0.1.00000000}.{GUID}
                // We can extract some meaningful information from this

                if (deviceId.Contains("\\")) {
                    // Extract the last part after the backslash
                    var parts = deviceId.Split('\\');
                    if (parts.Length > 1) {
                        var lastPart = parts[parts.Length - 1];
                        // Remove GUID-like parts and clean up
                        var cleanName = lastPart.Replace("{", "").Replace("}", "").Replace(".", " ");
                        if (cleanName.Length > 0) {
                            return cleanName.Trim();
                        }
                    }
                }

                // Fallback: use a shortened version of the device ID
                if (deviceId.Length > 20) {
                    return deviceId.Substring(0, 20) + "...";
                }

                return deviceId;
            }
            catch {
                return "Unknown Device";
            }
        }

        private List<AudioSession> GetSessionsFromDevice(IMMDevice mmDevice, AudioDiscoveryConfig config, string deviceName) {
            var sessions = new List<AudioSession>();

            try {
                // Activate the AudioSessionManager2 interface on the specified device
                // This interface provides access to audio sessions for this particular device
                LogDetailed("Activating AudioSessionManager2 for device: {DeviceName}", deviceName);
                IntPtr sessionManager;
                Guid sessionManagerGuid = IID_IAudioSessionManager2;
                int hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
                LogDetailed("AudioSessionManager2 activation returned HRESULT: 0x{Hr:X8} for device: {DeviceName}", hr, deviceName);

                if (hr != 0) {
                    LogDetailedWarning("Failed to activate AudioSessionManager2 for device: {DeviceName}, HRESULT: 0x{Hr:X8}", deviceName, hr);
                    return sessions;
                }

                // Convert the COM interface pointer to a .NET interface object
                var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;
                LogDetailed("Successfully created IAudioSessionManager2 interface for device: {DeviceName}", deviceName);

                // Get the session enumerator to iterate through all audio sessions on this device
                LogDetailed("Getting session enumerator for device: {DeviceName}", deviceName);
                IntPtr sessionEnumerator;
                hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
                LogDetailed("GetSessionEnumerator returned HRESULT: 0x{Hr:X8} for device: {DeviceName}", hr, deviceName);

                if (hr != 0) {
                    LogDetailedWarning("Failed to get session enumerator for device: {DeviceName}, HRESULT: 0x{Hr:X8}", deviceName, hr);
                    return sessions;
                }

                // Convert the session enumerator COM interface pointer to a .NET interface object
                var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;
                LogDetailed("Successfully created IAudioSessionEnumerator interface for device: {DeviceName}", deviceName);

                // Get the total number of audio sessions on this device
                LogDetailed("Getting session count for device: {DeviceName}", deviceName);
                int sessionCount;
                sessionEnum.GetCount(out sessionCount);
                LogDetailed("Found {SessionCount} audio sessions on device: {DeviceName}", sessionCount, deviceName);

                // Iterate through each audio session on this device
                for (int i = 0; i < sessionCount; i++) {
                    LogDetailed("Processing session {SessionIndex}/{SessionCount} on device: {DeviceName}", i + 1, sessionCount, deviceName);
                    try {
                        // Get the session interface pointer for this specific session
                        IntPtr session;
                        sessionEnum.GetSession(i, out session);
                        LogDetailed("Retrieved session {SessionIndex} pointer: 0x{Ptr:X8} from device: {DeviceName}", i, session.ToInt64(), deviceName);

                        // Convert to IAudioSessionControl2 interface for extended session information
                        // This interface provides access to process ID, display name, state, etc.
                        var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;

                        // Also get ISimpleAudioVolume interface for volume and mute information
                        var simpleVolume = sessionControl as ISimpleAudioVolume;

                        if (sessionControl != null && simpleVolume != null) {
                            LogDetailed("Successfully created session control and volume interfaces for session {SessionIndex} on device: {DeviceName}", i, deviceName);

                            // Get the process ID that owns this audio session
                            // This is the key identifier we use to match sessions to processes
                            int processId;
                            sessionControl.GetProcessId(out processId);
                            LogDetailed("Session {SessionIndex} ProcessId: {ProcessId} on device: {DeviceName}", i, processId, deviceName);

                            // Get the current state of the audio session
                            // States: 0=Inactive, 1=Active, 2=Expired
                            int state;
                            sessionControl.GetState(out state);
                            LogDetailed("Session {SessionIndex} State: {State} on device: {DeviceName}", i, state, deviceName);

                            // Apply state filter if specified in the configuration
                            // This allows filtering sessions by their current state
                            if (config.StateFilter != AudioSessionStateFilter.All && (int)config.StateFilter != state) {
                                LogDetailed("Skipping session {SessionIndex} due to state filter: Expected={StateFilter}, Actual={State}", i, config.StateFilter, state);
                                continue;
                            }

                            // Get the display name for this session (usually the application name)
                            IntPtr displayNamePtr;
                            sessionControl.GetDisplayName(out displayNamePtr);
                            string displayName = GetStringFromPointer(displayNamePtr);
                            LogDetailed("Session {SessionIndex} DisplayName: '{DisplayName}' on device: {DeviceName}", i, displayName, deviceName);

                            // Get the icon path for this session (usually the application icon)
                            IntPtr iconPathPtr;
                            sessionControl.GetIconPath(out iconPathPtr);
                            string iconPath = GetStringFromPointer(iconPathPtr);
                            LogDetailed("Session {SessionIndex} IconPath: '{IconPath}' on device: {DeviceName}", i, iconPath, deviceName);

                            // Get the current volume level and mute state for this session
                            float volume;
                            bool isMuted;
                            simpleVolume.GetMasterVolume(out volume);
                            simpleVolume.GetMute(out isMuted);
                            LogDetailed("Session {SessionIndex} Volume: {Volume:P2}, Muted: {IsMuted} on device: {DeviceName}", i, volume, isMuted, deviceName);

                            // Get the process name from the process ID
                            // This converts the numeric process ID to a human-readable process name
                            string processName = GetProcessName(processId);
                            LogDetailed("Session {SessionIndex} ProcessName: '{ProcessName}' on device: {DeviceName}", i, processName, deviceName);

                            // Apply process name filter if specified in the configuration
                            // This allows filtering sessions by process name using exact matches or regex patterns
                            if (!MatchesProcessNameFilter(processName, config)) {
                                LogDetailed("Skipping session {SessionIndex} due to process name filter: ProcessName='{ProcessName}' did not match filters", i, processName);
                                continue;
                            }

                            // Create an AudioSession object with all the collected information
                            var audioSession = new AudioSession {
                                ProcessId = processId,
                                ProcessName = processName,
                                DisplayName = displayName,
                                DeviceName = deviceName,
                                Volume = volume,
                                IsMuted = isMuted,
                                SessionState = state,
                                IconPath = iconPath,
                                LastUpdated = DateTime.UtcNow
                            };

                            // Add the session to our collection
                            sessions.Add(audioSession);
                            LogDetailed("Successfully created AudioSession for session {SessionIndex}: ProcessId={ProcessId}, ProcessName='{ProcessName}', Volume={Volume:P2}, Muted={IsMuted} on device: {DeviceName}",
                                i, processId, processName, volume, isMuted, deviceName);
                        }
                        else {
                            LogDetailedWarning("Failed to create session control or volume interface for session {SessionIndex} on device: {DeviceName}", i, deviceName);
                        }

                        // Release the session control COM object to prevent memory leaks
                        if (sessionControl != null) {
                            Marshal.ReleaseComObject(sessionControl);
                            LogDetailed("Released COM object for session {SessionIndex} on device: {DeviceName}", i, deviceName);
                        }
                    }
                    catch (Exception ex) {
                        LogDetailedError(ex, "Error processing audio session {SessionIndex} on device: {DeviceName}", i, deviceName);
                        _logger.LogWarning(ex, "Error processing audio session {SessionIndex} on device: {DeviceName}", i, deviceName);
                    }
                }

                // Clean up COM objects to prevent memory leaks
                LogDetailed("Cleaning up COM objects for device: {DeviceName}", deviceName);
                if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
                if (sessionMgr != null) Marshal.ReleaseComObject(sessionMgr);
                LogDetailed("COM objects released successfully for device: {DeviceName}", deviceName);
            }
            catch (Exception ex) {
                LogDetailedError(ex, "Error getting sessions from device: {DeviceName}", deviceName);
                _logger.LogError(ex, "Error getting sessions from device: {DeviceName}", deviceName);
            }

            return sessions;
        }

        #endregion

        #region Default Audio Device Methods

        public async Task<DefaultAudioDeviceInfo?> GetDefaultAudioDeviceAsync(AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console) {
            return await Task.Run(() => ExecuteWithTimeout(() => GetDefaultAudioDeviceInternal(dataFlow, deviceRole), TimeSpan.FromSeconds(5)));
        }

        public async Task<bool> SetDefaultDeviceVolumeAsync(float volume, AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console) {
            return await Task.Run(() => ExecuteWithTimeout(() => SetDefaultDeviceVolumeInternal(volume, dataFlow, deviceRole), TimeSpan.FromSeconds(5)));
        }

        public async Task<bool> MuteDefaultDeviceAsync(bool mute, AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console) {
            return await Task.Run(() => ExecuteWithTimeout(() => MuteDefaultDeviceInternal(mute, dataFlow, deviceRole), TimeSpan.FromSeconds(5)));
        }

        private DefaultAudioDeviceInfo? GetDefaultAudioDeviceInternal(AudioDataFlow dataFlow, AudioDeviceRole deviceRole) {
            lock (_lock) {
                _logger.LogDebug("Getting default audio device info - DataFlow: {DataFlow}, Role: {DeviceRole}", dataFlow, deviceRole);

                try {
                    // Initialize COM
                    CoInitialize(IntPtr.Zero);

                    // Create the device enumerator
                    IntPtr enumeratorPtr;
                    Guid enumeratorClsid = CLSID_MMDeviceEnumerator;
                    Guid enumeratorIid = IID_IMMDeviceEnumerator;
                    int hr = CoCreateInstance(ref enumeratorClsid, IntPtr.Zero, 1, ref enumeratorIid, out enumeratorPtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to create IMMDeviceEnumerator, HRESULT: 0x{Hr:X8}", hr);
                        return null;
                    }

                    var enumerator = Marshal.GetObjectForIUnknown(enumeratorPtr) as IMMDeviceEnumerator;
                    if (enumerator == null) {
                        _logger.LogError("Failed to cast to IMMDeviceEnumerator interface");
                        return null;
                    }

                    // Get the default audio endpoint
                    IntPtr device;
                    hr = enumerator.GetDefaultAudioEndpoint((int)dataFlow, (int)deviceRole, out device);

                    if (hr != 0) {
                        _logger.LogWarning("Failed to get default audio endpoint, HRESULT: 0x{Hr:X8}", hr);
                        return null;
                    }

                    var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
                    if (mmDevice == null) {
                        _logger.LogError("Failed to cast to IMMDevice interface");
                        return null;
                    }

                    // Get device ID
                    IntPtr deviceIdPtr;
                    mmDevice.GetId(out deviceIdPtr);
                    string deviceId = GetStringFromPointer(deviceIdPtr);

                    // Get device friendly name
                    string friendlyName = GetDeviceFriendlyName(mmDevice);
                    string deviceName = GetDeviceName(mmDevice);

                    // Get volume and mute state
                    var volumeInfo = GetDeviceVolumeInfo(mmDevice);

                    var deviceInfo = new DefaultAudioDeviceInfo {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        FriendlyName = friendlyName,
                        Volume = volumeInfo.Volume,
                        IsMuted = volumeInfo.IsMuted,
                        DataFlow = dataFlow,
                        DeviceRole = deviceRole
                    };

                    _logger.LogDebug("Successfully retrieved default device info: {DeviceName} - Volume: {Volume:P2}, Muted: {IsMuted}",
                        deviceName, volumeInfo.Volume, volumeInfo.IsMuted);

                    // Clean up COM objects
                    if (mmDevice != null) Marshal.ReleaseComObject(mmDevice);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);

                    return deviceInfo;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error getting default audio device info");
                    return null;
                }
                finally {
                    CoUninitialize();
                }
            }
        }

        private bool SetDefaultDeviceVolumeInternal(float volume, AudioDataFlow dataFlow, AudioDeviceRole deviceRole) {
            lock (_lock) {
                _logger.LogDebug("Setting default device volume: {Volume:P2} - DataFlow: {DataFlow}, Role: {DeviceRole}", volume, dataFlow, deviceRole);

                try {
                    // Clamp volume to valid range
                    volume = Math.Max(0.0f, Math.Min(1.0f, volume));

                    // Initialize COM
                    CoInitialize(IntPtr.Zero);

                    // Create the device enumerator
                    IntPtr enumeratorPtr;
                    Guid enumeratorClsid = CLSID_MMDeviceEnumerator;
                    Guid enumeratorIid = IID_IMMDeviceEnumerator;
                    int hr = CoCreateInstance(ref enumeratorClsid, IntPtr.Zero, 1, ref enumeratorIid, out enumeratorPtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to create IMMDeviceEnumerator, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var enumerator = Marshal.GetObjectForIUnknown(enumeratorPtr) as IMMDeviceEnumerator;
                    if (enumerator == null) {
                        _logger.LogError("Failed to cast to IMMDeviceEnumerator interface");
                        return false;
                    }

                    // Get the default audio endpoint
                    IntPtr device;
                    hr = enumerator.GetDefaultAudioEndpoint((int)dataFlow, (int)deviceRole, out device);

                    if (hr != 0) {
                        _logger.LogWarning("Failed to get default audio endpoint, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
                    if (mmDevice == null) {
                        _logger.LogError("Failed to cast to IMMDevice interface");
                        return false;
                    }

                    // Activate the IAudioEndpointVolume interface
                    IntPtr endpointVolumePtr;
                    Guid endpointVolumeGuid = IID_IAudioEndpointVolume;
                    hr = mmDevice.Activate(ref endpointVolumeGuid, 1, IntPtr.Zero, out endpointVolumePtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to activate IAudioEndpointVolume, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var endpointVolume = Marshal.GetObjectForIUnknown(endpointVolumePtr) as IAudioEndpointVolume;
                    if (endpointVolume == null) {
                        _logger.LogError("Failed to cast to IAudioEndpointVolume interface");
                        return false;
                    }

                    // Set the volume
                    Guid eventContext = Guid.Empty;
                    hr = endpointVolume.SetMasterVolumeLevelScalar(volume, ref eventContext);

                    if (hr != 0) {
                        _logger.LogError("Failed to set master volume, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    _logger.LogInformation("Successfully set default device volume to {Volume:P2}", volume);

                    // Clean up COM objects
                    if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                    if (mmDevice != null) Marshal.ReleaseComObject(mmDevice);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);

                    return true;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error setting default device volume");
                    return false;
                }
                finally {
                    CoUninitialize();
                }
            }
        }

        private bool MuteDefaultDeviceInternal(bool mute, AudioDataFlow dataFlow, AudioDeviceRole deviceRole) {
            lock (_lock) {
                _logger.LogDebug("Setting default device mute: {Mute} - DataFlow: {DataFlow}, Role: {DeviceRole}", mute, dataFlow, deviceRole);

                try {
                    // Initialize COM
                    CoInitialize(IntPtr.Zero);

                    // Create the device enumerator
                    IntPtr enumeratorPtr;
                    Guid enumeratorClsid = CLSID_MMDeviceEnumerator;
                    Guid enumeratorIid = IID_IMMDeviceEnumerator;
                    int hr = CoCreateInstance(ref enumeratorClsid, IntPtr.Zero, 1, ref enumeratorIid, out enumeratorPtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to create IMMDeviceEnumerator, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var enumerator = Marshal.GetObjectForIUnknown(enumeratorPtr) as IMMDeviceEnumerator;
                    if (enumerator == null) {
                        _logger.LogError("Failed to cast to IMMDeviceEnumerator interface");
                        return false;
                    }

                    // Get the default audio endpoint
                    IntPtr device;
                    hr = enumerator.GetDefaultAudioEndpoint((int)dataFlow, (int)deviceRole, out device);

                    if (hr != 0) {
                        _logger.LogWarning("Failed to get default audio endpoint, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
                    if (mmDevice == null) {
                        _logger.LogError("Failed to cast to IMMDevice interface");
                        return false;
                    }

                    // Activate the IAudioEndpointVolume interface
                    IntPtr endpointVolumePtr;
                    Guid endpointVolumeGuid = IID_IAudioEndpointVolume;
                    hr = mmDevice.Activate(ref endpointVolumeGuid, 1, IntPtr.Zero, out endpointVolumePtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to activate IAudioEndpointVolume, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var endpointVolume = Marshal.GetObjectForIUnknown(endpointVolumePtr) as IAudioEndpointVolume;
                    if (endpointVolume == null) {
                        _logger.LogError("Failed to cast to IAudioEndpointVolume interface");
                        return false;
                    }

                    // Set the mute state
                    Guid eventContext = Guid.Empty;
                    hr = endpointVolume.SetMute(mute, ref eventContext);

                    if (hr != 0) {
                        _logger.LogError("Failed to set mute state, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    _logger.LogInformation("Successfully set default device mute to {Mute}", mute);

                    // Clean up COM objects
                    if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                    if (mmDevice != null) Marshal.ReleaseComObject(mmDevice);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);

                    return true;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error setting default device mute");
                    return false;
                }
                finally {
                    CoUninitialize();
                }
            }
        }

        private string GetDeviceFriendlyName(IMMDevice device) {
            try {
                IntPtr propertyStorePtr;
                int hr = device.OpenPropertyStore(0, out propertyStorePtr); // 0 = STGM_READ

                if (hr != 0) {
                    return "Unknown Device";
                }

                var propertyStore = Marshal.GetObjectForIUnknown(propertyStorePtr) as IPropertyStore;
                if (propertyStore == null) {
                    return "Unknown Device";
                }

                PropertyVariant value;
                PropertyKey key = PKEY_Device_FriendlyName;
                hr = propertyStore.GetValue(ref key, out value);

                if (hr == 0 && value.vt == 31) // VT_LPWSTR
                {
                    string friendlyName = Marshal.PtrToStringUni(value.pwszVal);
                    if (propertyStore != null) Marshal.ReleaseComObject(propertyStore);
                    return friendlyName ?? "Unknown Device";
                }

                if (propertyStore != null) Marshal.ReleaseComObject(propertyStore);
                return "Unknown Device";
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Error getting device friendly name");
                return "Unknown Device";
            }
        }

        private (float Volume, bool IsMuted) GetDeviceVolumeInfo(IMMDevice device) {
            try {
                // Activate the IAudioEndpointVolume interface
                IntPtr endpointVolumePtr;
                Guid endpointVolumeGuid = IID_IAudioEndpointVolume;
                int hr = device.Activate(ref endpointVolumeGuid, 1, IntPtr.Zero, out endpointVolumePtr);

                if (hr != 0) {
                    return (0.0f, false);
                }

                var endpointVolume = Marshal.GetObjectForIUnknown(endpointVolumePtr) as IAudioEndpointVolume;
                if (endpointVolume == null) {
                    return (0.0f, false);
                }

                float volume;
                bool muted;
                endpointVolume.GetMasterVolumeLevelScalar(out volume);
                endpointVolume.GetMute(out muted);

                if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);

                return (volume, muted);
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Error getting device volume info");
                return (0.0f, false);
            }
        }

        #endregion

        #region Device Management by FriendlyName

        public async Task<bool> SetDeviceVolumeByFriendlyNameAsync(string deviceFriendlyName, float volume, AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console) {
            return await Task.Run(() => SetDeviceVolumeByFriendlyNameInternal(deviceFriendlyName, volume, dataFlow, deviceRole));
        }

        public async Task<bool> MuteDeviceByFriendlyNameAsync(string deviceFriendlyName, bool mute, AudioDataFlow dataFlow = AudioDataFlow.Render, AudioDeviceRole deviceRole = AudioDeviceRole.Console) {
            return await Task.Run(() => MuteDeviceByFriendlyNameInternal(deviceFriendlyName, mute, dataFlow, deviceRole));
        }

        private bool SetDeviceVolumeByFriendlyNameInternal(string deviceFriendlyName, float volume, AudioDataFlow dataFlow, AudioDeviceRole deviceRole) {
            lock (_lock) {
                _logger.LogDebug("Setting device volume by friendly name: {DeviceName}, Volume: {Volume:P0}", deviceFriendlyName, volume);

                try {
                    // Initialize COM
                    CoInitialize(IntPtr.Zero);

                    // Create the device enumerator
                    IntPtr enumeratorPtr;
                    Guid enumeratorClsid = CLSID_MMDeviceEnumerator;
                    Guid enumeratorIid = IID_IMMDeviceEnumerator;
                    int hr = CoCreateInstance(ref enumeratorClsid, IntPtr.Zero, 1, ref enumeratorIid, out enumeratorPtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to create IMMDeviceEnumerator, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var enumerator = Marshal.GetObjectForIUnknown(enumeratorPtr) as IMMDeviceEnumerator;
                    if (enumerator == null) {
                        _logger.LogError("Failed to cast to IMMDeviceEnumerator interface");
                        return false;
                    }

                    // Find the device by friendly name
                    var device = FindDeviceByFriendlyName(enumerator, deviceFriendlyName, dataFlow);
                    if (device == null) {
                        _logger.LogWarning("Device not found with friendly name: {DeviceName}", deviceFriendlyName);
                        return false;
                    }

                    // Activate the IAudioEndpointVolume interface
                    IntPtr endpointVolumePtr;
                    Guid endpointVolumeGuid = IID_IAudioEndpointVolume;
                    hr = device.Activate(ref endpointVolumeGuid, 1, IntPtr.Zero, out endpointVolumePtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to activate IAudioEndpointVolume, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var endpointVolume = Marshal.GetObjectForIUnknown(endpointVolumePtr) as IAudioEndpointVolume;
                    if (endpointVolume == null) {
                        _logger.LogError("Failed to cast to IAudioEndpointVolume interface");
                        return false;
                    }

                    // Set the volume
                    Guid eventContext = Guid.Empty;
                    hr = endpointVolume.SetMasterVolumeLevelScalar(volume, ref eventContext);

                    if (hr != 0) {
                        _logger.LogError("Failed to set volume, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    _logger.LogInformation("Successfully set device volume to {Volume:P0} for {DeviceName}", volume, deviceFriendlyName);

                    // Clean up COM objects
                    if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                    if (device != null) Marshal.ReleaseComObject(device);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);

                    return true;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error setting device volume by friendly name");
                    return false;
                }
                finally {
                    CoUninitialize();
                }
            }
        }

        private bool MuteDeviceByFriendlyNameInternal(string deviceFriendlyName, bool mute, AudioDataFlow dataFlow, AudioDeviceRole deviceRole) {
            lock (_lock) {
                _logger.LogDebug("Setting device mute by friendly name: {DeviceName}, Mute: {Mute}", deviceFriendlyName, mute);

                try {
                    // Initialize COM
                    CoInitialize(IntPtr.Zero);

                    // Create the device enumerator
                    IntPtr enumeratorPtr;
                    Guid enumeratorClsid = CLSID_MMDeviceEnumerator;
                    Guid enumeratorIid = IID_IMMDeviceEnumerator;
                    int hr = CoCreateInstance(ref enumeratorClsid, IntPtr.Zero, 1, ref enumeratorIid, out enumeratorPtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to create IMMDeviceEnumerator, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var enumerator = Marshal.GetObjectForIUnknown(enumeratorPtr) as IMMDeviceEnumerator;
                    if (enumerator == null) {
                        _logger.LogError("Failed to cast to IMMDeviceEnumerator interface");
                        return false;
                    }

                    // Find the device by friendly name
                    var device = FindDeviceByFriendlyName(enumerator, deviceFriendlyName, dataFlow);
                    if (device == null) {
                        _logger.LogWarning("Device not found with friendly name: {DeviceName}", deviceFriendlyName);
                        return false;
                    }

                    // Activate the IAudioEndpointVolume interface
                    IntPtr endpointVolumePtr;
                    Guid endpointVolumeGuid = IID_IAudioEndpointVolume;
                    hr = device.Activate(ref endpointVolumeGuid, 1, IntPtr.Zero, out endpointVolumePtr);

                    if (hr != 0) {
                        _logger.LogError("Failed to activate IAudioEndpointVolume, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    var endpointVolume = Marshal.GetObjectForIUnknown(endpointVolumePtr) as IAudioEndpointVolume;
                    if (endpointVolume == null) {
                        _logger.LogError("Failed to cast to IAudioEndpointVolume interface");
                        return false;
                    }

                    // Set the mute state
                    Guid eventContext = Guid.Empty;
                    hr = endpointVolume.SetMute(mute, ref eventContext);

                    if (hr != 0) {
                        _logger.LogError("Failed to set mute state, HRESULT: 0x{Hr:X8}", hr);
                        return false;
                    }

                    _logger.LogInformation("Successfully set device mute to {Mute} for {DeviceName}", mute, deviceFriendlyName);

                    // Clean up COM objects
                    if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                    if (device != null) Marshal.ReleaseComObject(device);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);

                    return true;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error setting device mute by friendly name");
                    return false;
                }
                finally {
                    CoUninitialize();
                }
            }
        }

        private IMMDevice? FindDeviceByFriendlyName(IMMDeviceEnumerator enumerator, string friendlyName, AudioDataFlow dataFlow) {
            try {
                // Get all devices of the specified data flow
                IntPtr devicesPtr;
                int hr = enumerator.EnumAudioEndpoints((int)dataFlow, 1, out devicesPtr); // 1 = DEVICE_STATE_ACTIVE

                if (hr != 0) {
                    _logger.LogError("Failed to enumerate devices, HRESULT: 0x{Hr:X8}", hr);
                    return null;
                }

                var devices = Marshal.GetObjectForIUnknown(devicesPtr) as IMMDeviceCollection;
                if (devices == null) {
                    _logger.LogError("Failed to cast to IMMDeviceCollection interface");
                    return null;
                }

                int deviceCount;
                hr = devices.GetCount(out deviceCount);
                if (hr != 0) {
                    _logger.LogError("Failed to get device count, HRESULT: 0x{Hr:X8}", hr);
                    return null;
                }

                // Iterate through devices to find matching friendly name
                for (int i = 0; i < deviceCount; i++) {
                    IntPtr devicePtr;
                    hr = devices.Item(i, out devicePtr);
                    if (hr != 0) continue;

                    var device = Marshal.GetObjectForIUnknown(devicePtr) as IMMDevice;
                    if (device == null) continue;

                    string deviceFriendlyName = GetDeviceFriendlyName(device);
                    if (string.Equals(deviceFriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase)) {
                        // Found the device, clean up collection and return the device
                        if (devices != null) Marshal.ReleaseComObject(devices);
                        return device;
                    }

                    // Release this device and continue searching
                    Marshal.ReleaseComObject(device);
                }

                // Clean up collection
                if (devices != null) Marshal.ReleaseComObject(devices);
                return null;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error finding device by friendly name: {FriendlyName}", friendlyName);
                return null;
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                    // Dispose managed resources
                }

                _disposed = true;
            }
        }

        #endregion
    }
}
