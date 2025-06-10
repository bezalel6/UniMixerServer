using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class AudioSession
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; }
    public string DisplayName { get; set; }
    public float Volume { get; set; }
    public bool IsMuted { get; set; }
    public int SessionState { get; set; }
    public string IconPath { get; set; }
}

public class ProcessVolumeManager
{
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

    // Audio session states
    public enum AudioSessionState
    {
        AudioSessionStateInactive = 0,
        AudioSessionStateActive = 1,
        AudioSessionStateExpired = 2
    }

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
        // Inherited methods from IAudioSessionControl
        new int GetState(out int state);
        new int GetDisplayName(out IntPtr name);
        new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        new int GetIconPath(out IntPtr path);
        new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        new int GetGroupingParam(out Guid groupingParam);
        new int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        new int RegisterAudioSessionNotification(IntPtr client);
        new int UnregisterAudioSessionNotification(IntPtr client);
        
        // IAudioSessionControl2 specific methods
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
            return process.ProcessName;
        }
        catch
        {
            return $"Unknown Process (PID: {processId})";
        }
    }

    public static List<AudioSession> GetAllAudioSessions()
    {
        var sessions = new List<AudioSession>();
        
        try
        {
            CoInitialize(IntPtr.Zero);

            // Create device enumerator
            IntPtr deviceEnumerator;
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iid = IID_IMMDeviceEnumerator;
            
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
            if (hr != 0) return sessions;

            var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;
            
            // Get default audio endpoint
            IntPtr device;
            hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device); // eRender, eConsole
            if (hr != 0) return sessions;

            var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
            
            // Activate session manager
            IntPtr sessionManager;
            Guid sessionManagerGuid = IID_IAudioSessionManager2;
            hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
            if (hr != 0) return sessions;

            var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;
            
            // Get session enumerator
            IntPtr sessionEnumerator;
            hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
            if (hr != 0) return sessions;

            var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;
            
            // Enumerate sessions
            int sessionCount;
            sessionEnum.GetCount(out sessionCount);

            for (int i = 0; i < sessionCount; i++)
            {
                try
                {
                    IntPtr session;
                    sessionEnum.GetSession(i, out session);
                    
                    var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;
                    var simpleVolume = sessionControl as ISimpleAudioVolume;
                    
                    if (sessionControl != null && simpleVolume != null)
                    {
                        // Get process ID
                        int processId;
                        sessionControl.GetProcessId(out processId);
                        
                        // Get session state
                        int state;
                        sessionControl.GetState(out state);
                        
                        // Get display name
                        IntPtr displayNamePtr;
                        sessionControl.GetDisplayName(out displayNamePtr);
                        string displayName = GetStringFromPointer(displayNamePtr);
                        
                        // Get icon path
                        IntPtr iconPathPtr;
                        sessionControl.GetIconPath(out iconPathPtr);
                        string iconPath = GetStringFromPointer(iconPathPtr);
                        
                        // Get volume and mute status
                        float volume;
                        bool isMuted;
                        simpleVolume.GetMasterVolume(out volume);
                        simpleVolume.GetMute(out isMuted);
                        
                        var audioSession = new AudioSession
                        {
                            ProcessId = processId,
                            ProcessName = GetProcessName(processId),
                            DisplayName = displayName,
                            Volume = volume,
                            IsMuted = isMuted,
                            SessionState = state,
                            IconPath = iconPath
                        };
                        
                        sessions.Add(audioSession);
                    }
                    
                    if (sessionControl != null)
                        Marshal.ReleaseComObject(sessionControl);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing session {i}: {ex.Message}");
                }
            }
            
            // Cleanup COM objects
            if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
            if (sessionMgr != null) Marshal.ReleaseComObject(sessionMgr);
            if (mmDevice != null) Marshal.ReleaseComObject(mmDevice);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting audio sessions: {ex.Message}");
        }
        finally
        {
            CoUninitialize();
        }
        
        return sessions;
    }

    public static bool SetProcessVolume(int processId, float volume)
    {
        try
        {
            // Clamp volume between 0.0 and 1.0
            volume = Math.Max(0.0f, Math.Min(1.0f, volume));

            CoInitialize(IntPtr.Zero);

            // Create device enumerator
            IntPtr deviceEnumerator;
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iid = IID_IMMDeviceEnumerator;
            
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
            if (hr != 0) return false;

            var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;
            
            // Get default audio endpoint
            IntPtr device;
            hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
            if (hr != 0) return false;

            var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
            
            // Activate session manager
            IntPtr sessionManager;
            Guid sessionManagerGuid = IID_IAudioSessionManager2;
            hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
            if (hr != 0) return false;

            var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;
            
            // Get session enumerator
            IntPtr sessionEnumerator;
            hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
            if (hr != 0) return false;

            var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;
            
            int sessionCount;
            sessionEnum.GetCount(out sessionCount);

            for (int i = 0; i < sessionCount; i++)
            {
                IntPtr session;
                sessionEnum.GetSession(i, out session);
                
                var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;
                
                int sessionProcessId;
                sessionControl.GetProcessId(out sessionProcessId);
                
                if (sessionProcessId == processId)
                {
                    var simpleVolume = sessionControl as ISimpleAudioVolume;
                    if (simpleVolume != null)
                    {
                        Guid eventContext = Guid.Empty;
                        hr = simpleVolume.SetMasterVolume(volume, ref eventContext);
                        return hr == 0;
                    }
                }
                
                Marshal.ReleaseComObject(sessionControl);
            }
            
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            CoUninitialize();
        }
    }

    public static bool MuteProcess(int processId, bool mute)
    {
        try
        {
            CoInitialize(IntPtr.Zero);

            // Similar implementation to SetProcessVolume but calls SetMute
            IntPtr deviceEnumerator;
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iid = IID_IMMDeviceEnumerator;
            
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out deviceEnumerator);
            if (hr != 0) return false;

            var enumerator = Marshal.GetObjectForIUnknown(deviceEnumerator) as IMMDeviceEnumerator;
            
            IntPtr device;
            hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
            if (hr != 0) return false;

            var mmDevice = Marshal.GetObjectForIUnknown(device) as IMMDevice;
            
            IntPtr sessionManager;
            Guid sessionManagerGuid = IID_IAudioSessionManager2;
            hr = mmDevice.Activate(ref sessionManagerGuid, 1, IntPtr.Zero, out sessionManager);
            if (hr != 0) return false;

            var sessionMgr = Marshal.GetObjectForIUnknown(sessionManager) as IAudioSessionManager2;
            
            IntPtr sessionEnumerator;
            hr = sessionMgr.GetSessionEnumerator(out sessionEnumerator);
            if (hr != 0) return false;

            var sessionEnum = Marshal.GetObjectForIUnknown(sessionEnumerator) as IAudioSessionEnumerator;
            
            int sessionCount;
            sessionEnum.GetCount(out sessionCount);

            for (int i = 0; i < sessionCount; i++)
            {
                IntPtr session;
                sessionEnum.GetSession(i, out session);
                
                var sessionControl = Marshal.GetObjectForIUnknown(session) as IAudioSessionControl2;
                
                int sessionProcessId;
                sessionControl.GetProcessId(out sessionProcessId);
                
                if (sessionProcessId == processId)
                {
                    var simpleVolume = sessionControl as ISimpleAudioVolume;
                    if (simpleVolume != null)
                    {
                        Guid eventContext = Guid.Empty;
                        hr = simpleVolume.SetMute(mute, ref eventContext);
                        return hr == 0;
                    }
                }
                
                Marshal.ReleaseComObject(sessionControl);
            }
            
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            CoUninitialize();
        }
    }

    // Example usage and demonstration
    public static void Main()
    {
        Console.WriteLine("=== Audio Session Manager ===\n");

        // Get all active audio sessions
        var sessions = GetAllAudioSessions();
        
        Console.WriteLine($"Found {sessions.Count} active audio sessions:\n");
        
        foreach (var session in sessions)
        {
            string stateText = ((AudioSessionState)session.SessionState).ToString();
            Console.WriteLine($"Process: {session.ProcessName} (PID: {session.ProcessId})");
            Console.WriteLine($"  Display Name: {session.DisplayName}");
            Console.WriteLine($"  Volume: {session.Volume:P0}");
            Console.WriteLine($"  Muted: {session.IsMuted}");
            Console.WriteLine($"  State: {stateText}");
            Console.WriteLine($"  Icon Path: {session.IconPath}");
            Console.WriteLine();
        }

        // Example: Set volume for a specific process
        Console.WriteLine("=== Volume Control Examples ===");
        
        // Find a process to control (example with notepad)
        var notepadSessions = sessions.FindAll(s => s.ProcessName.ToLower().Contains("notepad"));
        if (notepadSessions.Count > 0)
        {
            int notepadPid = notepadSessions[0].ProcessId;
            Console.WriteLine($"Setting Notepad volume to 50%...");
            bool success = SetProcessVolume(notepadPid, 0.5f);
            Console.WriteLine($"Result: {(success ? "Success" : "Failed")}");
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}