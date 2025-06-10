using System;
using System.Threading.Tasks;
using UniMixerServer.Core;
using Microsoft.Extensions.Logging;

namespace UniMixerServer.Examples
{
    /// <summary>
    /// Example demonstrating how to use the new process name filtering functionality in AudioManager.
    /// </summary>
    public class ProcessFilteringExample
    {
        public static async Task DemonstrateProcessFiltering()
        {
            // Create logger (using console logger for example)
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<AudioManager>();

            // Create AudioManager instance
            using var audioManager = new AudioManager(logger, enableDetailedLogging: true);

            Console.WriteLine("=== Process Name Filtering Examples ===\n");

            // Example 1: Filter by exact process names
            Console.WriteLine("1. Filtering by exact process names (chrome, firefox, spotify):");
            var exactConfig = new AudioDiscoveryConfig
            {
                ProcessNameFilters = new[] { "chrome", "firefox", "spotify" },
                UseRegexFiltering = false,
                VerboseLogging = true
            };

            var exactSessions = await audioManager.GetAllAudioSessionsAsync(exactConfig);
            Console.WriteLine($"Found {exactSessions.Count} sessions matching exact filters:");
            foreach (var session in exactSessions)
            {
                Console.WriteLine($"  - {session}");
            }
            Console.WriteLine();

            // Example 2: Filter using regex patterns
            Console.WriteLine("2. Filtering using regex patterns (^chrome.*, .*player.*):");
            var regexConfig = new AudioDiscoveryConfig
            {
                ProcessNameFilters = new[] { "^chrome.*", ".*player.*" },
                UseRegexFiltering = true,
                StateFilter = AudioSessionStateFilter.Active,
                VerboseLogging = true
            };

            var regexSessions = await audioManager.GetAllAudioSessionsAsync(regexConfig);
            Console.WriteLine($"Found {regexSessions.Count} sessions matching regex filters:");
            foreach (var session in regexSessions)
            {
                Console.WriteLine($"  - {session}");
            }
            Console.WriteLine();

            // Example 3: Filter for music/media applications
            Console.WriteLine("3. Filtering for music/media applications:");
            var musicConfig = new AudioDiscoveryConfig
            {
                ProcessNameFilters = new[] {
                    "(spotify|vlc|winamp|foobar2000|itunes|musicbee)",
                    ".*player.*",
                    ".*music.*"
                },
                UseRegexFiltering = true,
                VerboseLogging = true
            };

            var musicSessions = await audioManager.GetAllAudioSessionsAsync(musicConfig);
            Console.WriteLine($"Found {musicSessions.Count} music/media sessions:");
            foreach (var session in musicSessions)
            {
                Console.WriteLine($"  - {session}");
            }
            Console.WriteLine();

            // Example 4: Get all sessions (no filtering) for comparison
            Console.WriteLine("4. All audio sessions (no filtering):");
            var allSessions = await audioManager.GetAllAudioSessionsAsync();
            Console.WriteLine($"Found {allSessions.Count} total sessions:");
            foreach (var session in allSessions)
            {
                Console.WriteLine($"  - {session}");
            }
        }
    }
}

/* 
Common Usage Patterns:

// 1. Simple exact matching for specific applications
var config = new AudioDiscoveryConfig
{
    ProcessNameFilters = new[] { "chrome", "firefox", "spotify" },
    UseRegexFiltering = false
};

// 2. Browser-only sessions using regex
var browserConfig = new AudioDiscoveryConfig
{
    ProcessNameFilters = new[] { "(chrome|firefox|edge|safari|opera)" },
    UseRegexFiltering = true
};

// 3. Media players using wildcard patterns
var mediaConfig = new AudioDiscoveryConfig
{
    ProcessNameFilters = new[] { ".*player.*", "spotify", "itunes" },
    UseRegexFiltering = true
};

// 4. Games (case-insensitive matching)
var gameConfig = new AudioDiscoveryConfig
{
    ProcessNameFilters = new[] { "(?i).*game.*", "steam.*", ".*launcher.*" },
    UseRegexFiltering = true
};

Common Regex Patterns:
- "^chrome.*" - Processes starting with "chrome"
- ".*player.*" - Processes containing "player"
- "(app1|app2|app3)" - Any of the specified apps
- "(?i)discord" - Case-insensitive match
- "^(?!system).*audio.*" - Audio processes not starting with "system"
*/ 