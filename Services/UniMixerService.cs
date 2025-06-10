using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UniMixerServer.Communication;
using UniMixerServer.Configuration;
using UniMixerServer.Core;
using UniMixerServer.Models;

namespace UniMixerServer.Services
{
    public class UniMixerService : BackgroundService
    {
        private readonly ILogger<UniMixerService> _logger;
        private readonly AppConfig _config;
        private readonly IAudioManager _audioManager;
        private readonly List<ICommunicationHandler> _communicationHandlers;
        private Timer? _statusTimer;
        private Timer? _audioRefreshTimer;
        private List<UniMixerServer.Core.AudioSession> _lastKnownSessions = new List<UniMixerServer.Core.AudioSession>();

        public UniMixerService(
            ILogger<UniMixerService> logger,
            IOptions<AppConfig> config,
            IAudioManager audioManager,
            IEnumerable<ICommunicationHandler> communicationHandlers)
        {
            _logger = logger;
            _config = config.Value;
            _audioManager = audioManager;
            _communicationHandlers = communicationHandlers.ToList();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("UniMixer Service starting...");

            try
            {
                // Start communication handlers
                await StartCommunicationHandlersAsync(stoppingToken);

                // Setup event handlers
                SetupEventHandlers();

                // Start timers
                StartTimers();

                _logger.LogInformation("UniMixer Service started successfully");

                // Keep the service running until cancellation is requested
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("UniMixer Service stopping...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in UniMixer Service");
                throw;
            }
            finally
            {
                await StopAsync(stoppingToken);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("UniMixer Service stopping...");

            try
            {
                // Stop timers
                _statusTimer?.Dispose();
                _audioRefreshTimer?.Dispose();

                // Stop communication handlers
                await StopCommunicationHandlersAsync(cancellationToken);

                _logger.LogInformation("UniMixer Service stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping UniMixer Service");
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task StartCommunicationHandlersAsync(CancellationToken cancellationToken)
        {
            var startTasks = new List<Task>();

            foreach (var handler in _communicationHandlers)
            {
                try
                {
                    _logger.LogInformation("Starting communication handler: {HandlerName}", handler.Name);
                    startTasks.Add(handler.StartAsync(cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start communication handler: {HandlerName}", handler.Name);
                }
            }

            if (startTasks.Any())
            {
                await Task.WhenAll(startTasks);
            }
        }

        private async Task StopCommunicationHandlersAsync(CancellationToken cancellationToken)
        {
            var stopTasks = new List<Task>();

            foreach (var handler in _communicationHandlers)
            {
                try
                {
                    _logger.LogInformation("Stopping communication handler: {HandlerName}", handler.Name);
                    stopTasks.Add(handler.StopAsync(cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping communication handler: {HandlerName}", handler.Name);
                }
            }

            if (stopTasks.Any())
            {
                await Task.WhenAll(stopTasks);
            }
        }

        private void SetupEventHandlers()
        {
            // Subscribe to command events from all communication handlers
            foreach (var handler in _communicationHandlers)
            {
                handler.CommandReceived += OnCommandReceived;
                handler.ConnectionStatusChanged += OnConnectionStatusChanged;
            }

            // Subscribe to audio session changes
            _audioManager.AudioSessionChanged += OnAudioSessionChanged;
        }

        private void StartTimers()
        {
            // Status broadcast timer
            _statusTimer = new Timer(
                OnStatusTimerElapsed,
                null,
                TimeSpan.FromMilliseconds(_config.StatusBroadcastIntervalMs),
                TimeSpan.FromMilliseconds(_config.StatusBroadcastIntervalMs));

            // Audio session refresh timer
            _audioRefreshTimer = new Timer(
                OnAudioRefreshTimerElapsed,
                null,
                TimeSpan.FromMilliseconds(_config.AudioSessionRefreshIntervalMs),
                TimeSpan.FromMilliseconds(_config.AudioSessionRefreshIntervalMs));

            _logger.LogInformation("Started timers - Status: {StatusInterval}ms, Audio Refresh: {AudioInterval}ms",
                _config.StatusBroadcastIntervalMs, _config.AudioSessionRefreshIntervalMs);
        }

        private async void OnStatusTimerElapsed(object? state)
        {
            try
            {
                await BroadcastStatusAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during status broadcast");
            }
        }

        private async void OnAudioRefreshTimerElapsed(object? state)
        {
            try
            {
                var config = CreateAudioDiscoveryConfig();
                var sessions = await _audioManager.GetAllAudioSessionsAsync(config);

                // Check if sessions have changed
                if (HasSessionsChanged(sessions))
                {
                    _lastKnownSessions = sessions;
                    _logger.LogDebug("Audio sessions changed, triggering status update");
                    await BroadcastStatusAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during audio session refresh");
            }
        }

        private bool HasSessionsChanged(List<UniMixerServer.Core.AudioSession> newSessions)
        {
            if (_lastKnownSessions.Count != newSessions.Count)
                return true;

            // Compare each session
            foreach (var newSession in newSessions)
            {
                var oldSession = _lastKnownSessions.FirstOrDefault(s => s.ProcessName == newSession.ProcessName);
                if (oldSession == null)
                    return true;

                if (Math.Abs(oldSession.Volume - newSession.Volume) > 0.01f ||
                    oldSession.IsMuted != newSession.IsMuted ||
                    oldSession.SessionState != newSession.SessionState)
                    return true;
            }

            return false;
        }

        private async Task BroadcastStatusAsync()
        {
            try
            {
                var config = CreateAudioDiscoveryConfig();
                var sessions = await _audioManager.GetAllAudioSessionsAsync(config);
                foreach (var session in sessions)
                {
                    _logger.LogDebug("Session: {Session}", session.ToString());
                }

                // Filter out invalid sessions
                var validSessions = sessions.Where(s =>
                    s.ProcessId > 0 && !string.IsNullOrWhiteSpace(s.ProcessName)
                ).ToList();

                if (validSessions.Count != sessions.Count)
                {
                    _logger.LogWarning("Filtered out {FilteredCount} invalid sessions",
                        sessions.Count - validSessions.Count);
                }

                // Get default audio device information
                var defaultDevice = await GetDefaultAudioDeviceInfoAsync();

                var statusMessage = new StatusMessage
                {
                    DeviceId = _config.DeviceId,
                    Timestamp = DateTime.UtcNow,
                    ActiveSessionCount = validSessions.Count,
                    Sessions = validSessions.Select(s => new SessionStatus
                    {
                        ProcessId = s.ProcessId,
                        ProcessName = s.ProcessName ?? string.Empty,
                        DisplayName = s.DisplayName ?? string.Empty,
                        Volume = Math.Max(0.0f, Math.Min(1.0f, s.Volume)),
                        IsMuted = s.IsMuted,
                        State = ((AudioSessionState)s.SessionState).ToString()
                    }).ToList(),
                    DefaultDevice = defaultDevice
                };

                // Broadcast to all connected communication handlers
                var connectedHandlers = _communicationHandlers.Where(h => h.IsConnected).ToList();
                var broadcastTasks = connectedHandlers.Select(h => h.SendStatusAsync(statusMessage));

                await Task.WhenAll(broadcastTasks);

                // Log detailed default device information
                if (defaultDevice != null)
                {
                    _logger.LogInformation("Status sent to {HandlerCount} handlers, {SessionCount} sessions\n" +
                        "Default Audio Device Details:\n" +
                        "  Device ID: {DeviceId}\n" +
                        "  Device Name: {DeviceName}\n" +
                        "  Friendly Name: {FriendlyName}\n" +
                        "  Volume: {Volume:P1} ({VolumeRaw:F3})\n" +
                        "  Is Muted: {IsMuted}\n" +
                        "  Data Flow: {DataFlow}\n" +
                        "  Device Role: {DeviceRole}",
                        connectedHandlers.Count, validSessions.Count,
                        defaultDevice.DeviceId,
                        defaultDevice.DeviceName,
                        defaultDevice.FriendlyName,
                        defaultDevice.Volume, defaultDevice.Volume,
                        defaultDevice.IsMuted,
                        defaultDevice.DataFlow,
                        defaultDevice.DeviceRole);
                }
                else
                {
                    _logger.LogInformation("Status sent to {HandlerCount} handlers, {SessionCount} sessions\n" +
                        "Default Audio Device: None (No default device found)",
                        connectedHandlers.Count, validSessions.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting status");
            }
        }

        private async Task<DefaultAudioDevice?> GetDefaultAudioDeviceInfoAsync()
        {
            try
            {
                var deviceInfo = await _audioManager.GetDefaultAudioDeviceAsync();
                if (deviceInfo == null)
                    return null;

                return new DefaultAudioDevice
                {
                    DeviceId = deviceInfo.DeviceId,
                    DeviceName = deviceInfo.DeviceName,
                    FriendlyName = deviceInfo.FriendlyName,
                    Volume = deviceInfo.Volume,
                    IsMuted = deviceInfo.IsMuted,
                    DataFlow = deviceInfo.DataFlow.ToString(),
                    DeviceRole = deviceInfo.DeviceRole.ToString()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting default audio device info");
                return null;
            }
        }

        private UniMixerServer.Core.AudioDiscoveryConfig CreateAudioDiscoveryConfig()
        {
            return new UniMixerServer.Core.AudioDiscoveryConfig
            {
                IncludeAllDevices = _config.Audio.IncludeAllDevices,
                IncludeCaptureDevices = _config.Audio.IncludeCaptureDevices,
                DataFlow = ParseDataFlow(_config.Audio.DataFlow),
                DeviceRole = ParseDeviceRole(_config.Audio.DeviceRole),
                StateFilter = UniMixerServer.Core.AudioSessionStateFilter.All,
                VerboseLogging = _config.Audio.EnableDetailedLogging,
                ProcessNameFilters = _config.AllowedProcesses.ToArray()
            };
        }

        private UniMixerServer.Core.AudioDataFlow ParseDataFlow(string dataFlow)
        {
            return dataFlow?.ToLowerInvariant() switch
            {
                "render" => UniMixerServer.Core.AudioDataFlow.Render,
                "capture" => UniMixerServer.Core.AudioDataFlow.Capture,
                "all" => UniMixerServer.Core.AudioDataFlow.All,
                _ => UniMixerServer.Core.AudioDataFlow.Render
            };
        }

        private UniMixerServer.Core.AudioDeviceRole ParseDeviceRole(string deviceRole)
        {
            return deviceRole?.ToLowerInvariant() switch
            {
                "console" => UniMixerServer.Core.AudioDeviceRole.Console,
                "multimedia" => UniMixerServer.Core.AudioDeviceRole.Multimedia,
                "communications" => UniMixerServer.Core.AudioDeviceRole.Communications,
                _ => UniMixerServer.Core.AudioDeviceRole.Console
            };
        }

        private async void OnCommandReceived(object? sender, CommandReceivedEventArgs e)
        {
            try
            {
                _logger.LogInformation("Processing command {CommandType} from {Source}",
                    e.Command.CommandType, e.Source);

                await ProcessCommandAsync(e.Command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command");
            }
        }

        private async Task ProcessCommandAsync(AudioCommand command)
        {
            try
            {
                switch (command.CommandType)
                {
                    case AudioCommandType.SetVolume:
                        if (string.IsNullOrWhiteSpace(command.ProcessName))
                        {
                            _logger.LogWarning("Process name is required for SetVolume command");
                            break;
                        }

                        var volumeSuccess = await _audioManager.SetProcessVolumeByNameAsync(command.ProcessName, command.Volume);
                        if (volumeSuccess)
                        {
                            _logger.LogInformation("Volume set to {Volume:P0} for process {ProcessName}",
                                command.Volume, command.ProcessName);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to set volume for process {ProcessName} (no active audio session found)",
                                command.ProcessName);
                        }
                        break;

                    case AudioCommandType.Mute:
                        if (string.IsNullOrWhiteSpace(command.ProcessName))
                        {
                            _logger.LogWarning("Process name is required for Mute command");
                            break;
                        }

                        var muteSuccess = await _audioManager.MuteProcessByNameAsync(command.ProcessName, true);
                        if (muteSuccess)
                        {
                            _logger.LogInformation("Process {ProcessName} muted", command.ProcessName);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to mute process {ProcessName}", command.ProcessName);
                        }
                        break;

                    case AudioCommandType.Unmute:
                        if (string.IsNullOrWhiteSpace(command.ProcessName))
                        {
                            _logger.LogWarning("Process name is required for Unmute command");
                            break;
                        }

                        var unmuteSuccess = await _audioManager.MuteProcessByNameAsync(command.ProcessName, false);
                        if (unmuteSuccess)
                        {
                            _logger.LogInformation("Process {ProcessName} unmuted", command.ProcessName);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to unmute process {ProcessName}", command.ProcessName);
                        }
                        break;

                    case AudioCommandType.GetStatus:
                        await BroadcastStatusAsync();
                        break;

                    case AudioCommandType.GetAllSessions:
                        await BroadcastStatusAsync();
                        break;

                    case AudioCommandType.SetDefaultDeviceVolume:
                        var setVolumeSuccess = await _audioManager.SetDefaultDeviceVolumeAsync(command.Volume);
                        if (setVolumeSuccess)
                        {
                            _logger.LogInformation("Default device volume set to {Volume:P0}", command.Volume);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to set default device volume");
                        }
                        break;

                    case AudioCommandType.MuteDefaultDevice:
                        var muteDefaultSuccess = await _audioManager.MuteDefaultDeviceAsync(true);
                        if (muteDefaultSuccess)
                        {
                            _logger.LogInformation("Default device muted");
                        }
                        else
                        {
                            _logger.LogWarning("Failed to mute default device");
                        }
                        break;

                    case AudioCommandType.UnmuteDefaultDevice:
                        var unmuteDefaultSuccess = await _audioManager.MuteDefaultDeviceAsync(false);
                        if (unmuteDefaultSuccess)
                        {
                            _logger.LogInformation("Default device unmuted");
                        }
                        else
                        {
                            _logger.LogWarning("Failed to unmute default device");
                        }
                        break;

                    default:
                        _logger.LogWarning("Unknown command type: {CommandType}", command.CommandType);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing command {CommandType}", command.CommandType);
            }
        }

        private void OnConnectionStatusChanged(object? sender, ConnectionStatusChangedEventArgs e)
        {
            _logger.LogInformation("Connection status changed for {HandlerName}: {IsConnected} - {Message}",
                e.HandlerName, e.IsConnected ? "Connected" : "Disconnected", e.Message);
        }

        private void OnAudioSessionChanged(object? sender, AudioSessionChangedEventArgs e)
        {
            _logger.LogDebug("Audio sessions changed: {SessionCount} sessions", e.Sessions.Count);
            _lastKnownSessions = e.Sessions;
        }
    }
}