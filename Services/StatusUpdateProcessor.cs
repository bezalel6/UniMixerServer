using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Core;
using UniMixerServer.Models;

namespace UniMixerServer.Services {
    public class StatusUpdateProcessor {
        private readonly ILogger<StatusUpdateProcessor> _logger;
        private readonly IAudioManager _audioManager;

        public StatusUpdateProcessor(ILogger<StatusUpdateProcessor> logger, IAudioManager audioManager) {
            _logger = logger;
            _audioManager = audioManager;
        }

        public async Task<StatusUpdateResult> ProcessUpdateAsync(
            StatusUpdate statusUpdate, 
            List<UniMixerServer.Core.AudioSession> currentSessions, 
            DefaultAudioDevice? currentDefaultDevice) {
            
            var result = new StatusUpdateResult();
            
            try {
                // Process default device changes
                if (statusUpdate.DefaultDevice != null) {
                    await ProcessDefaultDeviceUpdatesAsync(statusUpdate.DefaultDevice, currentDefaultDevice, result);
                }

                // Process session updates
                await ProcessSessionUpdatesAsync(statusUpdate.Sessions, currentSessions, result);

                _logger.LogInformation("Status update processing complete: {ChangesApplied} changes applied, {ChangesSkipped} changes skipped (already in sync)",
                    result.ChangesApplied, result.ChangesSkipped);

                return result;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing status update");
                result.HasErrors = true;
                return result;
            }
        }

        private async Task ProcessDefaultDeviceUpdatesAsync(
            DefaultAudioDevice desiredDevice, 
            DefaultAudioDevice? currentDevice, 
            StatusUpdateResult result) {
            
            if (currentDevice == null) {
                _logger.LogWarning("Cannot update default device - no current default device found");
                return;
            }

            // Process volume update
            await ProcessVolumeUpdateAsync(
                "Default Device",
                currentDevice.Volume,
                desiredDevice.Volume,
                async (volume) => await _audioManager.SetDefaultDeviceVolumeAsync(volume),
                result);

            // Process mute update
            await ProcessMuteUpdateAsync(
                "Default Device",
                currentDevice.IsMuted,
                desiredDevice.IsMuted,
                async (isMuted) => await _audioManager.MuteDefaultDeviceAsync(isMuted),
                result);
        }

        private async Task ProcessSessionUpdatesAsync(
            List<SessionUpdate> sessionUpdates, 
            List<UniMixerServer.Core.AudioSession> currentSessions, 
            StatusUpdateResult result) {
            
            foreach (var sessionUpdate in sessionUpdates) {
                if (string.IsNullOrWhiteSpace(sessionUpdate.ProcessName)) {
                    continue;
                }

                var currentSession = currentSessions.FirstOrDefault(s => 
                    string.Equals(s.ProcessName, sessionUpdate.ProcessName, StringComparison.OrdinalIgnoreCase));

                if (currentSession == null) {
                    _logger.LogDebug("Process {ProcessName} not found in current sessions - skipping update", 
                        sessionUpdate.ProcessName);
                    continue;
                }

                // Process volume update
                await ProcessVolumeUpdateAsync(
                    sessionUpdate.ProcessName,
                    currentSession.Volume,
                    sessionUpdate.Volume,
                    async (volume) => await _audioManager.SetProcessVolumeByNameAsync(sessionUpdate.ProcessName, volume),
                    result);

                // Process mute update
                await ProcessMuteUpdateAsync(
                    sessionUpdate.ProcessName,
                    currentSession.IsMuted,
                    sessionUpdate.IsMuted,
                    async (isMuted) => await _audioManager.MuteProcessByNameAsync(sessionUpdate.ProcessName, isMuted),
                    result);
            }
        }

        private async Task ProcessVolumeUpdateAsync(
            string targetName,
            float currentVolume,
            float desiredVolume,
            Func<float, Task<bool>> updateAction,
            StatusUpdateResult result) {
            
            var needsUpdate = Math.Abs(currentVolume - desiredVolume) > 0.01f;
            
            if (!needsUpdate) {
                _logger.LogDebug("Skipping volume update for {TargetName} - already at {Volume:P1}", 
                    targetName, desiredVolume);
                result.ChangesSkipped++;
                return;
            }

            var success = await updateAction(desiredVolume);
            if (success) {
                _logger.LogInformation("Updated volume for {TargetName} from {OldVolume:P1} to {NewVolume:P1}",
                    targetName, currentVolume, desiredVolume);
                result.ChangesApplied++;
            }
            else {
                _logger.LogWarning("Failed to update volume for {TargetName}", targetName);
                result.FailedUpdates++;
            }
        }

        private async Task ProcessMuteUpdateAsync(
            string targetName,
            bool currentMuted,
            bool desiredMuted,
            Func<bool, Task<bool>> updateAction,
            StatusUpdateResult result) {
            
            if (currentMuted == desiredMuted) {
                _logger.LogDebug("Skipping mute update for {TargetName} - already {IsMuted}", 
                    targetName, desiredMuted);
                result.ChangesSkipped++;
                return;
            }

            var success = await updateAction(desiredMuted);
            if (success) {
                _logger.LogInformation("Updated mute state for {TargetName} from {OldMuted} to {NewMuted}",
                    targetName, currentMuted, desiredMuted);
                result.ChangesApplied++;
            }
            else {
                _logger.LogWarning("Failed to update mute state for {TargetName}", targetName);
                result.FailedUpdates++;
            }
        }
    }

    public class StatusUpdateResult {
        public int ChangesApplied { get; set; }
        public int ChangesSkipped { get; set; }
        public int FailedUpdates { get; set; }
        public bool HasErrors { get; set; }
        
        public bool HasChanges => ChangesApplied > 0;
        public bool IsSuccessful => !HasErrors && FailedUpdates == 0;
    }
} 