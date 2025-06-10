using System;
using System.Collections.Generic;
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
                var sessions = await _audioManager.GetAllAudioSessionsAsync();
                
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
                var oldSession = _lastKnownSessions.FirstOrDefault(s => s.ProcessId == newSession.ProcessId);
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
                var sessions = await _audioManager.GetAllAudioSessionsAsync();
                
                // Filter out invalid sessions and log them
                var validSessions = sessions.Where(s => 
                {
                    if (s.ProcessId <= 0 || string.IsNullOrWhiteSpace(s.ProcessName))
                    {
                        _logger.LogWarning("Filtering out invalid session: PID={ProcessId}, Name='{ProcessName}'", 
                            s.ProcessId, s.ProcessName);
                        return false;
                    }
                    return true;
                }).ToList();

                _logger.LogDebug("Filtered sessions: {ValidCount}/{TotalCount} sessions are valid", 
                    validSessions.Count, sessions.Count);

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
                    }).ToList()
                };

                // Broadcast to all connected communication handlers
                var broadcastTasks = _communicationHandlers
                    .Where(h => h.IsConnected)
                    .Select(h => h.SendStatusAsync(statusMessage));

                await Task.WhenAll(broadcastTasks);

                _logger.LogDebug("Status broadcasted to {HandlerCount} handlers", broadcastTasks.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting status");
            }
        }

        private async void OnCommandReceived(object? sender, CommandReceivedEventArgs e)
        {
            try
            {
                _logger.LogInformation("Processing command {CommandType} from {Source}", 
                    e.Command.CommandType, e.Source);

                var result = await ProcessCommandAsync(e.Command);

                // Send result back through the source handler
                if (sender is ICommunicationHandler handler)
                {
                    await handler.SendCommandResultAsync(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command");
                
                var errorResult = new CommandResult
                {
                    Success = false,
                    Message = $"Error processing command: {ex.Message}",
                    RequestId = e.Command.RequestId
                };

                if (sender is ICommunicationHandler handler)
                {
                    await handler.SendCommandResultAsync(errorResult);
                }
            }
        }

        private async Task<CommandResult> ProcessCommandAsync(AudioCommand command)
        {
            var result = new CommandResult
            {
                RequestId = command.RequestId,
                Success = false
            };

            try
            {
                switch (command.CommandType)
                {
                    case AudioCommandType.SetVolume:
                        result.Success = await _audioManager.SetProcessVolumeAsync(command.ProcessId, command.Volume);
                        result.Message = result.Success 
                            ? $"Volume set to {command.Volume:P0} for process {command.ProcessId}"
                            : $"Failed to set volume for process {command.ProcessId}";
                        break;

                    case AudioCommandType.Mute:
                        result.Success = await _audioManager.MuteProcessAsync(command.ProcessId, true);
                        result.Message = result.Success 
                            ? $"Process {command.ProcessId} muted"
                            : $"Failed to mute process {command.ProcessId}";
                        break;

                    case AudioCommandType.Unmute:
                        result.Success = await _audioManager.MuteProcessAsync(command.ProcessId, false);
                        result.Message = result.Success 
                            ? $"Process {command.ProcessId} unmuted"
                            : $"Failed to unmute process {command.ProcessId}";
                        break;

                    case AudioCommandType.GetStatus:
                        var sessions = await _audioManager.GetAllAudioSessionsAsync();
                        result.Success = true;
                        result.Message = $"Retrieved {sessions.Count} audio sessions";
                        result.Data = sessions;
                        break;

                    case AudioCommandType.GetAllSessions:
                        var allSessions = await _audioManager.GetAllAudioSessionsAsync();
                        result.Success = true;
                        result.Message = $"Retrieved {allSessions.Count} audio sessions";
                        result.Data = allSessions;
                        break;

                    default:
                        result.Message = $"Unknown command type: {command.CommandType}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Message = $"Error executing command: {ex.Message}";
                _logger.LogError(ex, "Error executing command {CommandType}", command.CommandType);
            }

            return result;
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