using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UniMixerServer.Core;

namespace UniMixerServer
{
    public class AudioManagerTester
    {
        public static async Task RunTest(string[] args)
        {
            Console.WriteLine("=== UniMixer AudioManager Tester ===");
            Console.WriteLine("This program will test the AudioManager with detailed logging enabled.");
            Console.WriteLine();

            // Create service collection and configure logging
            var services = new ServiceCollection();
            
            // Configure logging
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Build service provider
            var serviceProvider = services.BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<AudioManager>>();

            Console.WriteLine("Creating AudioManager with detailed logging enabled...");
            Console.WriteLine();

            // Create AudioManager with detailed logging enabled
            using var audioManager = new AudioManager(logger, enableDetailedLogging: true);

            try
            {
                Console.WriteLine("=== TESTING GetAllAudioSessionsAsync (Default Config) ===");
                Console.WriteLine();

                var sessions = await audioManager.GetAllAudioSessionsAsync();

                Console.WriteLine();
                Console.WriteLine($"=== DEFAULT CONFIG RESULTS ===");
                Console.WriteLine($"Total Sessions Found: {sessions.Count}");
                Console.WriteLine();

                if (sessions.Count > 0)
                {
                    Console.WriteLine("Session Details:");
                    Console.WriteLine("PID".PadRight(8) + "Process Name".PadRight(25) + "Display Name".PadRight(25) + "Volume".PadRight(10) + "Muted".PadRight(8) + "State");
                    Console.WriteLine(new string('-', 85));

                    foreach (var session in sessions)
                    {
                        var processName = session.ProcessName.Length > 24 ? session.ProcessName.Substring(0, 21) + "..." : session.ProcessName;
                        var displayName = session.DisplayName.Length > 24 ? session.DisplayName.Substring(0, 21) + "..." : session.DisplayName;
                        
                        Console.WriteLine(
                            session.ProcessId.ToString().PadRight(8) +
                            processName.PadRight(25) +
                            displayName.PadRight(25) +
                            $"{session.Volume:P0}".PadRight(10) +
                            session.IsMuted.ToString().PadRight(8) +
                            session.SessionState
                        );
                    }
                }

                Console.WriteLine();
                Console.WriteLine("=== TESTING WITH ALL DEVICES ENABLED ===");
                Console.WriteLine();

                var allDevicesConfig = new UniMixerServer.Core.AudioDiscoveryConfig
                {
                    IncludeAllDevices = true,
                    DataFlow = UniMixerServer.Core.AudioDataFlow.All,
                    StateFilter = UniMixerServer.Core.AudioSessionStateFilter.All
                };

                var allDevicesSessions = await audioManager.GetAllAudioSessionsAsync(allDevicesConfig);

                Console.WriteLine();
                Console.WriteLine($"=== ALL DEVICES CONFIG RESULTS ===");
                Console.WriteLine($"Total Sessions Found: {allDevicesSessions.Count}");
                Console.WriteLine();

                if (allDevicesSessions.Count > 0)
                {
                    Console.WriteLine("Session Details:");
                    Console.WriteLine("PID".PadRight(8) + "Process Name".PadRight(25) + "Display Name".PadRight(25) + "Volume".PadRight(10) + "Muted".PadRight(8) + "State");
                    Console.WriteLine(new string('-', 85));

                    foreach (var session in allDevicesSessions)
                    {
                        var processName = session.ProcessName.Length > 24 ? session.ProcessName.Substring(0, 21) + "..." : session.ProcessName;
                        var displayName = session.DisplayName.Length > 24 ? session.DisplayName.Substring(0, 21) + "..." : session.DisplayName;
                        
                        Console.WriteLine(
                            session.ProcessId.ToString().PadRight(8) +
                            processName.PadRight(25) +
                            displayName.PadRight(25) +
                            $"{session.Volume:P0}".PadRight(10) +
                            session.IsMuted.ToString().PadRight(8) +
                            session.SessionState
                        );
                    }
                }

                // Test with capture devices included
                Console.WriteLine();
                Console.WriteLine("=== TESTING WITH CAPTURE DEVICES INCLUDED ===");
                Console.WriteLine();

                var captureConfig = new UniMixerServer.Core.AudioDiscoveryConfig
                {
                    IncludeCaptureDevices = true,
                    DataFlow = UniMixerServer.Core.AudioDataFlow.Render,
                    StateFilter = UniMixerServer.Core.AudioSessionStateFilter.All
                };

                var captureSessions = await audioManager.GetAllAudioSessionsAsync(captureConfig);

                Console.WriteLine();
                Console.WriteLine($"=== CAPTURE DEVICES CONFIG RESULTS ===");
                Console.WriteLine($"Total Sessions Found: {captureSessions.Count}");
                Console.WriteLine();

                if (captureSessions.Count > 0)
                {
                    Console.WriteLine("Session Details:");
                    Console.WriteLine("PID".PadRight(8) + "Process Name".PadRight(25) + "Display Name".PadRight(25) + "Volume".PadRight(10) + "Muted".PadRight(8) + "State");
                    Console.WriteLine(new string('-', 85));

                    foreach (var session in captureSessions)
                    {
                        var processName = session.ProcessName.Length > 24 ? session.ProcessName.Substring(0, 21) + "..." : session.ProcessName;
                        var displayName = session.DisplayName.Length > 24 ? session.DisplayName.Substring(0, 21) + "..." : session.DisplayName;
                        
                        Console.WriteLine(
                            session.ProcessId.ToString().PadRight(8) +
                            processName.PadRight(25) +
                            displayName.PadRight(25) +
                            $"{session.Volume:P0}".PadRight(10) +
                            session.IsMuted.ToString().PadRight(8) +
                            session.SessionState
                        );
                    }
                }

                Console.WriteLine();
                Console.WriteLine("=== CONFIGURATION COMPARISON ===");
                Console.WriteLine($"Default config sessions: {sessions.Count}");
                Console.WriteLine($"All devices config sessions: {allDevicesSessions.Count}");
                Console.WriteLine($"With capture devices sessions: {captureSessions.Count}");
                Console.WriteLine();

                if (allDevicesSessions.Count > sessions.Count)
                {
                    Console.WriteLine("✅ Found additional sessions when scanning all devices!");
                    var additionalSessions = allDevicesSessions.Where(s => !sessions.Any(os => os.ProcessId == s.ProcessId && os.ProcessName == s.ProcessName)).ToList();
                    Console.WriteLine($"Additional sessions found: {additionalSessions.Count}");
                    foreach (var session in additionalSessions)
                    {
                        Console.WriteLine($"  - {session.ProcessName} (PID: {session.ProcessId}) - Volume: {session.Volume:P0}");
                    }
                }
                else
                {
                    Console.WriteLine("ℹ️  No additional sessions found when scanning all devices.");
                }

                Console.WriteLine();
                Console.WriteLine("=== TESTING VOLUME CONTROL ===");
                Console.WriteLine();

                if (sessions.Count > 0)
                {
                    // Test with the first session that has a valid process ID
                    var testSession = sessions.FirstOrDefault(s => s.ProcessId > 0);
                    if (testSession != null)
                    {
                        Console.WriteLine($"Testing volume control with: {testSession.ProcessName} (PID: {testSession.ProcessId})");
                        Console.WriteLine($"Current volume: {testSession.Volume:P2}");
                        Console.WriteLine();

                        // Test getting current volume
                        Console.WriteLine("=== Testing GetProcessVolumeAsync ===");
                        var currentVolume = await audioManager.GetProcessVolumeAsync(testSession.ProcessId);
                        Console.WriteLine($"Retrieved volume: {currentVolume:P2}");
                        Console.WriteLine();

                        // Test getting current mute state
                        Console.WriteLine("=== Testing GetProcessMuteStateAsync ===");
                        var currentMuteState = await audioManager.GetProcessMuteStateAsync(testSession.ProcessId);
                        Console.WriteLine($"Retrieved mute state: {currentMuteState}");
                        Console.WriteLine();

                        // Note: We won't actually change volume/mute for safety
                        Console.WriteLine("Note: Volume and mute changes are disabled for safety in this test.");
                        Console.WriteLine("To test these features, uncomment the relevant sections in the code.");
                        
                        /*
                        // Uncomment to test volume setting
                        Console.WriteLine("=== Testing SetProcessVolumeAsync ===");
                        var newVolume = Math.Max(0.1f, testSession.Volume * 0.8f); // Reduce by 20% but keep at least 10%
                        Console.WriteLine($"Setting volume to: {newVolume:P2}");
                        var volumeResult = await audioManager.SetProcessVolumeAsync(testSession.ProcessId, newVolume);
                        Console.WriteLine($"Volume set result: {volumeResult}");
                        
                        // Restore original volume
                        await Task.Delay(2000);
                        await audioManager.SetProcessVolumeAsync(testSession.ProcessId, testSession.Volume);
                        Console.WriteLine($"Restored volume to: {testSession.Volume:P2}");
                        */
                    }
                    else
                    {
                        Console.WriteLine("No suitable test session found (all sessions have ProcessId 0).");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("=== TEST COMPLETED SUCCESSFULLY ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"=== ERROR OCCURRED ===");
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
} 