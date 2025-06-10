using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#if WINDOWS
using System.Runtime.Versioning;
#endif

namespace UniMixerServer.Core
{
    // Add new enums for configuration
    public enum AudioDataFlow
    {
        Render = 0,      // Playback/Output devices
        Capture = 1,     // Recording/Input devices  
        All = 2          // Both input and output
    }

    public enum AudioDeviceRole
    {
        Console = 0,         // Default device for general use
        Multimedia = 1,      // Multimedia applications
        Communications = 2   // Voice communications
    }

    public enum AudioSessionStateFilter
    {
        All = -1,        // All sessions regardless of state
        Inactive = 0,    // Only inactive sessions
        Active = 1,      // Only active sessions
        Expired = 2      // Only expired sessions
    }

    public class AudioDiscoveryConfig
    {
        public AudioDataFlow DataFlow { get; set; } = AudioDataFlow.Render;
        public AudioDeviceRole DeviceRole { get; set; } = AudioDeviceRole.Console;
        public AudioSessionStateFilter StateFilter { get; set; } = AudioSessionStateFilter.All;
        public bool IncludeAllDevices { get; set; } = false;  // If true, scans ALL audio devices, not just default
        public bool IncludeCaptureDevices { get; set; } = false; // If true, also includes input devices
        public bool VerboseLogging { get; set; } = false;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
#endif
    public class AudioManager : IAudioManager, IDisposable
    {
        private readonly ILogger<AudioManager> _logger;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private readonly bool _enableDetailedLogging;

        public event EventHandler<AudioSessionChangedEventArgs>? AudioSessionChanged;

        public AudioManager(ILogger<AudioManager> logger, bool enableDetailedLogging = false)
        {
            _logger = logger;
            _enableDetailedLogging = enableDetailedLogging;
            
            if (_enableDetailedLogging)
            {
                _logger.LogInformation("AudioManager initialized with detailed logging enabled");
            }
        }

        private void LogDetailed(string message, params object[] args)
        {
            if (_enableDetailedLogging)
            {
                _logger.LogInformation(message, args);
            }
        }

        private void LogDetailedWarning(string message, params object[] args)
        {
            if (_enableDetailedLogging)
            {
                _logger.LogWarning(message, args);
            }
        }

        private void LogDetailedError(Exception ex, string message, params object[] args)
        {
            if (_enableDetailedLogging)
            {
                _logger.LogError(ex, message, args);
            }
        }

        #region COM Interop Declarations

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr ptr);

        [DllImport("ole32.dll")]
        static extern int CoInitialize(IntPtr pvReserved);

        [DllImport("ole32.dll")]
        static extern void CoUninitialize();

        // Core Audio API GUIDs
        static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr interfacePointer);
            int OpenPropertyStore(int stgmAccess, out IntPtr properties);
            int GetId(out IntPtr strId);
            int GetState(out int state);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            int GetAudioSessionControl(ref Guid groupingParam, int streamFlags, out IntPtr sessionControl);
            int GetSimpleAudioVolume(ref Guid groupingParam, int streamFlags, out IntPtr audioVolume);
            int GetSessionEnumerator(out IntPtr sessionEnum);
            int RegisterSessionNotification(IntPtr sessionNotification);
            int UnregisterSessionNotification(IntPtr sessionNotification);
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            int GetCount(out int sessionCount);
            int GetSession(int sessionNumber, out IntPtr session);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl
        {
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetIconPath(out IntPtr path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetGroupingParam(out Guid groupingParam);
            int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
        }

        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2 : IAudioSessionControl
        {
            new int GetState(out int state);
            new int GetDisplayName(out IntPtr name);
            new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            new int GetIconPath(out IntPtr path);
            new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            new int GetGroupingParam(out Guid groupingParam);
            new int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            new int RegisterAudioSessionNotification(IntPtr client);
            new int UnregisterAudioSessionNotification(IntPtr client);
            
            int GetSessionIdentifier(out IntPtr retVal);
            int GetSessionInstanceIdentifier(out IntPtr retVal);
            int GetProcessId(out int retVal);
            int IsSystemSoundsSession();
            int SetDuckingPreference(bool optOut);
        }

        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            int SetMasterVolume(float level, ref Guid eventContext);
            int GetMasterVolume(out float level);
            int SetMute(bool mute, ref Guid eventContext);
            int GetMute(out bool mute);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            int GetCount(out int deviceCount);
            int Item(int deviceNumber, out IntPtr device);
        }

        #endregion

        #region Helper Methods

        private static string GetStringFromPointer(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return string.Empty;

            try
            {
                string result = Marshal.PtrToStringUni(ptr);
                CoTaskMemFree(ptr);
                return result ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetProcessName(int processId)
        {
            try
            {
                if (processId == 0) return "System Sounds";

                var process = Process.GetProcessById(processId);
                var processName = process.ProcessName;
                
                // Sanitize process name - remove any invalid characters
                if (string.IsNullOrWhiteSpace(processName))
                {
                    return $"Unknown Process (PID: {processId})";
                }
                
                // Remove any control characters or invalid JSON characters
                processName = new string(processName.Where(c => !char.IsControl(c) && c != '"' && c != '\\').ToArray());
                
                // Limit length to prevent buffer issues
                if (processName.Length > 50)
                {
                    processName = processName.Substring(0, 50);
                }
                
                return string.IsNullOrWhiteSpace(processName) ? $"Process_{processId}" : processName;
            }
            catch
            {
                return $"Unknown Process (PID: {processId})";
            }
        }

        #endregion

        #region IAudioManager Implementation

        // Keep the original method for backward compatibility  
        public async Task<List<AudioSession>> GetAllAudioSessionsAsync()
        {
            return await GetAllAudioSessionsAsync(new AudioDiscoveryConfig());
        }

        public async Task<List<AudioSession>> GetAllAudioSessionsAsync(AudioDiscoveryConfig? config = null)
        {
            LogDetailed("Starting GetAllAudioSessionsAsync with custom config");
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    LogDetailed("Acquired lock for GetAllAudioSessionsAsync");
                    var result = GetAllAudioSessionsInternal(config ?? new AudioDiscoveryConfig());
                    LogDetailed("Completed GetAllAudioSessionsAsync, returning {SessionCount} sessions", result.Count);
                    return result;
                }
            });
        }

        public async Task<bool> SetProcessVolumeAsync(int processId, float volume)
        {
            LogDetailed("Starting SetProcessVolumeAsync for ProcessId: {ProcessId}, Volume: {Volume:P2}", processId, volume);
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    LogDetailed("Acquired lock for SetProcessVolumeAsync");
                    var result = SetProcessVolumeInternal(processId, volume);
                    LogDetailed("Completed SetProcessVolumeAsync with result: {Result}", result);
                    return result;
                }
            });
        }

        public async Task<bool> MuteProcessAsync(int processId, bool mute)
        {
            LogDetailed("Starting MuteProcessAsync for ProcessId: {ProcessId}, Mute: {Mute}", processId, mute);
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    LogDetailed("Acquired lock for MuteProcessAsync");
                    var result = MuteProcessInternal(processId, mute);
                    LogDetailed("Completed MuteProcessAsync with result: {Result}", result);
                    return result;
                }
            });
        }

        public async Task<float?> GetProcessVolumeAsync(int processId)
        {
            LogDetailed("Starting GetProcessVolumeAsync for ProcessId: {ProcessId}", processId);
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    LogDetailed("Acquired lock for GetProcessVolumeAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => s.ProcessId == processId);
                    var result = session?.Volume;
                    LogDetailed("Completed GetProcessVolumeAsync with result: {Result}", result?.ToString() ?? "null");
                    return result;
                }
            });
        }

        public async Task<bool?> GetProcessMuteStateAsync(int processId)
        {
            LogDetailed("Starting GetProcessMuteStateAsync for ProcessId: {ProcessId}", processId);
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    LogDetailed("Acquired lock for GetProcessMuteStateAsync");
                    var sessions = GetAllAudioSessionsInternal();
                    var session = sessions.FirstOrDefault(s => s.ProcessId == processId);
                    var result = session?.IsMuted;
                    LogDetailed("Completed GetProcessMuteStateAsync with result: {Result}", result?.ToString() ?? "null");
                    return result;
                }
            });
        }

        #endregion

        #region Internal Implementation

        private List<AudioSession> GetAllAudioSessionsInternal(AudioDiscoveryConfig? config = null)
        {
            config ??= new AudioDiscoveryConfig();
            LogDetailed("Starting GetAllAudioSessionsInternal with config: DataFlow={DataFlow}, Role={DeviceRole}, StateFilter={StateFilter}, IncludeAllDevices={IncludeAllDevices}", 
                config.DataFlow, config.DeviceRole, config.StateFilter, config.IncludeAllDevices);
            
            var sessions = new List<AudioSession>();

            try
            {
                LogDetailed("Initializing COM");
                CoInitialize(IntPtr.Zero);

                LogDetailed("Creating MMDeviceEnumerator instance");
                IntPtr deviceEnumerator;
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;

                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
                LogDetailed("CoCreateInstance for MMDeviceEnumerator returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) 
                {
                    LogDetailedWarning("Failed to create MMDeviceEnumerator, HRESULT: 0x{Hr:X8}", hr);
                    return sessions;
                }

                var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;
                LogDetailed("Successfully created IMMDeviceEnumerator interface");

                if (config.IncludeAllDevices)
                {
                    // Enumerate ALL audio devices
                    LogDetailed("Enumerating ALL audio devices");
                    sessions.AddRange(GetSessionsFromAllDevices(enumerator, config));
                }
                else
                {
                    // Get sessions from default device only (original behavior)
                    LogDetailed("Getting sessions from default audio endpoint only");
                    var defaultSessions = GetSessionsFromDefaultDevice(enumerator, config);
                    sessions.AddRange(defaultSessions);

                    // Optionally also include capture devices
                    if (config.IncludeCaptureDevices && config.DataFlow != AudioDataFlow.Capture)
                    {
                        LogDetailed("Also including capture device sessions");
                        var captureConfig = new AudioDiscoveryConfig
                        {
                            DataFlow = AudioDataFlow.Capture,
                            DeviceRole = config.DeviceRole,
                            StateFilter = config.StateFilter,
                            IncludeAllDevices = false,
                            VerboseLogging = config.VerboseLogging
                        };
                        var captureSessions = GetSessionsFromDefaultDevice(enumerator, captureConfig);
                        sessions.AddRange(captureSessions);
                    }
                }

                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            catch (Exception ex)
            {
                LogDetailedError(ex, "Error in GetAllAudioSessionsInternal");
                _logger.LogError(ex, "Error getting audio sessions");
            }
            finally
            {
                LogDetailed("Uninitializing COM");
                CoUninitialize();
            }

            LogDetailed("GetAllAudioSessionsInternal completed with {SessionCount} sessions", sessions.Count);
            
            // Log summary of all sessions
            if (_enableDetailedLogging && sessions.Any())
            {
                _logger.LogInformation("=== AUDIO SESSIONS SUMMARY ===");
                foreach (var session in sessions)
                {
                    _logger.LogInformation("Session: PID={ProcessId}, Name='{ProcessName}', Display='{DisplayName}', Volume={Volume:P2}, Muted={IsMuted}, State={SessionState}", 
                        session.ProcessId, session.ProcessName, session.DisplayName, session.Volume, session.IsMuted, session.SessionState);
                }
                _logger.LogInformation("=== END AUDIO SESSIONS SUMMARY ===");
            }

            return sessions;
        }

        private bool SetProcessVolumeInternal(int processId, float volume)
        {
            LogDetailed("Starting SetProcessVolumeInternal for ProcessId: {ProcessId}, Volume: {Volume:P2}", processId, volume);
            try
            {
                volume = Math.Max(0.0f, Math.Min(1.0f, volume));
                LogDetailed("Clamped volume to: {Volume:P2}", volume);
                
                CoInitialize(IntPtr.Zero);
                LogDetailed("COM initialized for SetProcessVolumeInternal");

                IntPtr deviceEnumerator;
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;

                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
                LogDetailed("CoCreateInstance returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;

                IntPtr device;
                hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                LogDetailed("GetDefaultAudioEndpoint returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;

                IntPtr sessionManager;
                Guid sessionManagerGuid = IID_IAudioSessionManager2;
                hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
                LogDetailed("AudioSessionManager2 activation returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;

                IntPtr sessionEnumerator;
                hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
                LogDetailed("GetSessionEnumerator returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;

                int sessionCount;
                sessionEnum.GetCount(out sessionCount);
                LogDetailed("Found {SessionCount} sessions to search", sessionCount);

                for (int i = 0; i < sessionCount; i++)
                {
                    IntPtr session;
                    sessionEnum.GetSession(i, out session);

                    var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;

                    int sessionProcessId;
                    sessionControl.GetProcessId(out sessionProcessId);
                    LogDetailed("Session {SessionIndex} has ProcessId: {SessionProcessId}", i, sessionProcessId);

                    if (sessionProcessId == processId)
                    {
                        LogDetailed("Found matching session for ProcessId: {ProcessId}", processId);
                        var simpleVolume = sessionControl as ISimpleAudioVolume;
                        if (simpleVolume != null)
                        {
                            Guid eventContext = Guid.Empty;
                            hr = simpleVolume.SetMasterVolume(volume, ref eventContext);
                            LogDetailed("SetMasterVolume returned HRESULT: 0x{Hr:X8}", hr);
                            
                            Marshal.ReleaseComObject(sessionControl);
                            _logger.LogInformation("Set volume for process {ProcessId} to {Volume:P0}", processId, volume);
                            LogDetailed("Successfully set volume for ProcessId: {ProcessId} to {Volume:P2}", processId, volume);
                            return hr == 0;
                        }
                        else
                        {
                            LogDetailedWarning("Failed to get ISimpleAudioVolume interface for ProcessId: {ProcessId}", processId);
                        }
                    }

                    Marshal.ReleaseComObject(sessionControl);
                }

                LogDetailedWarning("No matching session found for ProcessId: {ProcessId}", processId);
                return false;
            }
            catch (Exception ex)
            {
                LogDetailedError(ex, "Error in SetProcessVolumeInternal for ProcessId: {ProcessId}", processId);
                _logger.LogError(ex, "Error setting volume for process {ProcessId}", processId);
                return false;
            }
            finally
            {
                LogDetailed("Uninitializing COM for SetProcessVolumeInternal");
                CoUninitialize();
            }
        }

        private bool MuteProcessInternal(int processId, bool mute)
        {
            LogDetailed("Starting MuteProcessInternal for ProcessId: {ProcessId}, Mute: {Mute}", processId, mute);
            try
            {
                CoInitialize(IntPtr.Zero);
                LogDetailed("COM initialized for MuteProcessInternal");

                IntPtr deviceEnumerator;
                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;

                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
                LogDetailed("CoCreateInstance returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;

                IntPtr device;
                hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                LogDetailed("GetDefaultAudioEndpoint returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;

                IntPtr sessionManager;
                Guid sessionManagerGuid = IID_IAudioSessionManager2;
                hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
                LogDetailed("AudioSessionManager2 activation returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;

                IntPtr sessionEnumerator;
                hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
                LogDetailed("GetSessionEnumerator returned HRESULT: 0x{Hr:X8}", hr);
                if (hr != 0) return false;

                var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;

                int sessionCount;
                sessionEnum.GetCount(out sessionCount);
                LogDetailed("Found {SessionCount} sessions to search", sessionCount);

                for (int i = 0; i < sessionCount; i++)
                {
                    IntPtr session;
                    sessionEnum.GetSession(i, out session);

                    var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;

                    int sessionProcessId;
                    sessionControl.GetProcessId(out sessionProcessId);
                    LogDetailed("Session {SessionIndex} has ProcessId: {SessionProcessId}", i, sessionProcessId);

                    if (sessionProcessId == processId)
                    {
                        LogDetailed("Found matching session for ProcessId: {ProcessId}", processId);
                        var simpleVolume = sessionControl as ISimpleAudioVolume;
                        if (simpleVolume != null)
                        {
                            Guid eventContext = Guid.Empty;
                            hr = simpleVolume.SetMute(mute, ref eventContext);
                            LogDetailed("SetMute returned HRESULT: 0x{Hr:X8}", hr);
                            
                            Marshal.ReleaseComObject(sessionControl);
                            _logger.LogInformation("Set mute for process {ProcessId} to {Mute}", processId, mute);
                            LogDetailed("Successfully set mute for ProcessId: {ProcessId} to {Mute}", processId, mute);
                            return hr == 0;
                        }
                        else
                        {
                            LogDetailedWarning("Failed to get ISimpleAudioVolume interface for ProcessId: {ProcessId}", processId);
                        }
                    }

                    Marshal.ReleaseComObject(sessionControl);
                }

                LogDetailedWarning("No matching session found for ProcessId: {ProcessId}", processId);
                return false;
            }
            catch (Exception ex)
            {
                LogDetailedError(ex, "Error in MuteProcessInternal for ProcessId: {ProcessId}", processId);
                _logger.LogError(ex, "Error setting mute for process {ProcessId}", processId);
                return false;
            }
            finally
            {
                LogDetailed("Uninitializing COM for MuteProcessInternal");
                CoUninitialize();
            }
        }

        private List<AudioSession> GetSessionsFromDefaultDevice(IMMDeviceEnumerator enumerator, AudioDiscoveryConfig config)
        {
            var sessions = new List<AudioSession>();
            
            LogDetailed("Getting default audio endpoint for DataFlow={DataFlow}, Role={DeviceRole}", config.DataFlow, config.DeviceRole);
            IntPtr device;
            int hr = enumerator.GetDefaultAudioEndpoint((int)config.DataFlow, (int)config.DeviceRole, out device);
            LogDetailed("GetDefaultAudioEndpoint returned HRESULT: 0x{Hr:X8}", hr);
            
            if (hr != 0) 
            {
                LogDetailedWarning("Failed to get default audio endpoint, HRESULT: 0x{Hr:X8}", hr);
                return sessions;
            }

            var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
            LogDetailed("Successfully created IMMDevice interface");

            var deviceSessions = GetSessionsFromDevice(mmDevice, config, "Default Device");
            sessions.AddRange(deviceSessions);

            if (mmDevice != null) Marshal.ReleaseComObject(mmDevice);
            
            return sessions;
        }

        private List<AudioSession> GetSessionsFromAllDevices(IMMDeviceEnumerator enumerator, AudioDiscoveryConfig config)
        {
            var sessions = new List<AudioSession>();
            
            // Enumerate devices for the specified data flow
            var dataFlows = new List<AudioDataFlow>();
            if (config.DataFlow == AudioDataFlow.All)
            {
                dataFlows.Add(AudioDataFlow.Render);
                dataFlows.Add(AudioDataFlow.Capture);
            }
            else
            {
                dataFlows.Add(config.DataFlow);
            }

            foreach (var dataFlow in dataFlows)
            {
                LogDetailed("Enumerating devices for DataFlow: {DataFlow}", dataFlow);
                
                IntPtr deviceCollection;
                int hr = enumerator.EnumAudioEndpoints((int)dataFlow, 1, out deviceCollection); // 1 = DEVICE_STATE_ACTIVE
                LogDetailed("EnumAudioEndpoints returned HRESULT: 0x{Hr:X8}", hr);
                
                if (hr != 0) continue;

                var collection = Marshal.GetObjectForIUnknown(deviceCollection) as IMMDeviceCollection;
                if (collection == null) continue;

                int deviceCount;
                collection.GetCount(out deviceCount);
                LogDetailed("Found {DeviceCount} active {DataFlow} devices", deviceCount, dataFlow);

                for (int i = 0; i < deviceCount; i++)
                {
                    IntPtr device;
                    hr = collection.Item(i, out device);
                    if (hr != 0) continue;

                    var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
                    if (mmDevice == null) continue;

                    // Get device name for logging
                    string deviceName = GetDeviceName(mmDevice);
                    LogDetailed("Processing device {DeviceIndex}/{DeviceCount}: {DeviceName}", i + 1, deviceCount, deviceName);

                    var deviceSessions = GetSessionsFromDevice(mmDevice, config, $"{dataFlow} Device {i}: {deviceName}");
                    sessions.AddRange(deviceSessions);

                    Marshal.ReleaseComObject(mmDevice);
                }

                Marshal.ReleaseComObject(collection);
            }

            return sessions;
        }

        private string GetDeviceName(IMMDevice device)
        {
            try
            {
                IntPtr propertyStore;
                int hr = device.OpenPropertyStore(0, out propertyStore); // 0 = STGM_READ
                if (hr != 0) return "Unknown Device";

                // Property store operations would go here to get friendly name
                // For now, just return a placeholder
                return "Audio Device";
            }
            catch
            {
                return "Unknown Device";
            }
        }

        private List<AudioSession> GetSessionsFromDevice(IMMDevice mmDevice, AudioDiscoveryConfig config, string deviceName)
        {
            var sessions = new List<AudioSession>();

            try
            {
                LogDetailed("Activating AudioSessionManager2 for device: {DeviceName}", deviceName);
                IntPtr sessionManager;
                Guid sessionManagerGuid = IID_IAudioSessionManager2;
                int hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
                LogDetailed("AudioSessionManager2 activation returned HRESULT: 0x{Hr:X8} for device: {DeviceName}", hr, deviceName);
                
                if (hr != 0) 
                {
                    LogDetailedWarning("Failed to activate AudioSessionManager2 for device: {DeviceName}, HRESULT: 0x{Hr:X8}", deviceName, hr);
                    return sessions;
                }

                var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;
                LogDetailed("Successfully created IAudioSessionManager2 interface for device: {DeviceName}", deviceName);

                LogDetailed("Getting session enumerator for device: {DeviceName}", deviceName);
                IntPtr sessionEnumerator;
                hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
                LogDetailed("GetSessionEnumerator returned HRESULT: 0x{Hr:X8} for device: {DeviceName}", hr, deviceName);
                
                if (hr != 0) 
                {
                    LogDetailedWarning("Failed to get session enumerator for device: {DeviceName}, HRESULT: 0x{Hr:X8}", deviceName, hr);
                    return sessions;
                }

                var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;
                LogDetailed("Successfully created IAudioSessionEnumerator interface for device: {DeviceName}", deviceName);

                LogDetailed("Getting session count for device: {DeviceName}", deviceName);
                int sessionCount;
                sessionEnum.GetCount(out sessionCount);
                LogDetailed("Found {SessionCount} audio sessions on device: {DeviceName}", sessionCount, deviceName);

                for (int i = 0; i < sessionCount; i++)
                {
                    LogDetailed("Processing session {SessionIndex}/{SessionCount} on device: {DeviceName}", i + 1, sessionCount, deviceName);
                    try
                    {
                        IntPtr session;
                        sessionEnum.GetSession(i, out session);
                        LogDetailed("Retrieved session {SessionIndex} pointer: 0x{Ptr:X8} from device: {DeviceName}", i, session.ToInt64(), deviceName);

                        var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;
                        var simpleVolume = sessionControl as ISimpleAudioVolume;

                        if (sessionControl != null && simpleVolume != null)
                        {
                            LogDetailed("Successfully created session control and volume interfaces for session {SessionIndex} on device: {DeviceName}", i, deviceName);

                            int processId;
                            sessionControl.GetProcessId(out processId);
                            LogDetailed("Session {SessionIndex} ProcessId: {ProcessId} on device: {DeviceName}", i, processId, deviceName);

                            int state;
                            sessionControl.GetState(out state);
                            LogDetailed("Session {SessionIndex} State: {State} on device: {DeviceName}", i, state, deviceName);

                            // Apply state filter
                            if (config.StateFilter != AudioSessionStateFilter.All && (int)config.StateFilter != state)
                            {
                                LogDetailed("Skipping session {SessionIndex} due to state filter: Expected={StateFilter}, Actual={State}", i, config.StateFilter, state);
                                continue;
                            }

                            IntPtr displayNamePtr;
                            sessionControl.GetDisplayName(out displayNamePtr);
                            string displayName = GetStringFromPointer(displayNamePtr);
                            LogDetailed("Session {SessionIndex} DisplayName: '{DisplayName}' on device: {DeviceName}", i, displayName, deviceName);

                            IntPtr iconPathPtr;
                            sessionControl.GetIconPath(out iconPathPtr);
                            string iconPath = GetStringFromPointer(iconPathPtr);
                            LogDetailed("Session {SessionIndex} IconPath: '{IconPath}' on device: {DeviceName}", i, iconPath, deviceName);

                            float volume;
                            bool isMuted;
                            simpleVolume.GetMasterVolume(out volume);
                            simpleVolume.GetMute(out isMuted);
                            LogDetailed("Session {SessionIndex} Volume: {Volume:P2}, Muted: {IsMuted} on device: {DeviceName}", i, volume, isMuted, deviceName);

                            string processName = GetProcessName(processId);
                            LogDetailed("Session {SessionIndex} ProcessName: '{ProcessName}' on device: {DeviceName}", i, processName, deviceName);

                            var audioSession = new AudioSession
                            {
                                ProcessId = processId,
                                ProcessName = processName,
                                DisplayName = displayName,
                                Volume = volume,
                                IsMuted = isMuted,
                                SessionState = state,
                                IconPath = iconPath,
                                LastUpdated = DateTime.UtcNow
                            };

                            sessions.Add(audioSession);
                            LogDetailed("Successfully created AudioSession for session {SessionIndex}: ProcessId={ProcessId}, ProcessName='{ProcessName}', Volume={Volume:P2}, Muted={IsMuted} on device: {DeviceName}", 
                                i, processId, processName, volume, isMuted, deviceName);
                        }
                        else
                        {
                            LogDetailedWarning("Failed to create session control or volume interface for session {SessionIndex} on device: {DeviceName}", i, deviceName);
                        }

                        if (sessionControl != null)
                        {
                            Marshal.ReleaseComObject(sessionControl);
                            LogDetailed("Released COM object for session {SessionIndex} on device: {DeviceName}", i, deviceName);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDetailedError(ex, "Error processing audio session {SessionIndex} on device: {DeviceName}", i, deviceName);
                        _logger.LogWarning(ex, "Error processing audio session {SessionIndex} on device: {DeviceName}", i, deviceName);
                    }
                }

                LogDetailed("Cleaning up COM objects for device: {DeviceName}", deviceName);
                if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
                if (sessionMgr != null) Marshal.ReleaseComObject(sessionMgr);
                LogDetailed("COM objects released successfully for device: {DeviceName}", deviceName);
            }
            catch (Exception ex)
            {
                LogDetailedError(ex, "Error getting sessions from device: {DeviceName}", deviceName);
                _logger.LogError(ex, "Error getting sessions from device: {DeviceName}", deviceName);
            }

            return sessions;
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                }

                _disposed = true;
            }
        }

        #endregion
    }
} 