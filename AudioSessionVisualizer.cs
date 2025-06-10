using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UniMixerServer.Core;

namespace UniMixerServer
{
    public static class AudioSessionVisualizer
    {
        public static void DisplayDeviceTree(List<AudioSession> sessions, string title = "Audio Sessions Device Tree")
        {
            Console.WriteLine();
            Console.WriteLine($"┌─ {title} ─┐");
            Console.WriteLine("│");
            
            if (!sessions.Any())
            {
                Console.WriteLine("├─ No audio sessions found");
                Console.WriteLine("└─");
                return;
            }

            // Group by process for better visualization
            var processGroups = sessions.GroupBy(s => new { s.ProcessId, s.ProcessName })
                                      .OrderBy(g => g.Key.ProcessName)
                                      .ToList();

            for (int i = 0; i < processGroups.Count; i++)
            {
                var group = processGroups[i];
                var isLast = i == processGroups.Count - 1;
                var connector = isLast ? "└─" : "├─";
                
                var sessionsInGroup = group.ToList();
                var avgVolume = sessionsInGroup.Average(s => s.Volume);
                var isMuted = sessionsInGroup.Any(s => s.IsMuted);
                var states = string.Join(",", sessionsInGroup.Select(s => s.SessionState).Distinct());
                
                Console.WriteLine($"{connector} 🎵 {group.Key.ProcessName} (PID: {group.Key.ProcessId})");
                
                var volumeBar = CreateVolumeBar(avgVolume, 20);
                var muteIndicator = isMuted ? "🔇" : "🔊";
                var childConnector = isLast ? "    " : "│   ";
                
                Console.WriteLine($"{childConnector}├─ Volume: {volumeBar} {avgVolume:P0} {muteIndicator}");
                Console.WriteLine($"{childConnector}├─ Sessions: {sessionsInGroup.Count} (States: {states})");
                
                if (sessionsInGroup.Count > 1)
                {
                    Console.WriteLine($"{childConnector}└─ Session Details:");
                    for (int j = 0; j < sessionsInGroup.Count; j++)
                    {
                        var session = sessionsInGroup[j];
                        var sessionConnector = j == sessionsInGroup.Count - 1 ? "└─" : "├─";
                        var displayName = string.IsNullOrEmpty(session.DisplayName) ? "[No Display Name]" : session.DisplayName;
                        Console.WriteLine($"{childConnector}   {sessionConnector} {displayName} (Vol: {session.Volume:P0}, State: {session.SessionState})");
                    }
                }
                else
                {
                    var session = sessionsInGroup[0];
                    var displayName = string.IsNullOrEmpty(session.DisplayName) ? "[No Display Name]" : session.DisplayName;
                    Console.WriteLine($"{childConnector}└─ Display: {displayName}");
                }
                
                if (!isLast) Console.WriteLine("│");
            }
            
            Console.WriteLine("└─");
        }

        public static void DisplayVolumeChart(List<AudioSession> sessions, string title = "Audio Session Volume Levels")
        {
            Console.WriteLine();
            Console.WriteLine($"┌─ {title} ─┐");
            Console.WriteLine();
            
            if (!sessions.Any())
            {
                Console.WriteLine("No audio sessions to display");
                return;
            }

            var processGroups = sessions.GroupBy(s => new { s.ProcessId, s.ProcessName })
                                      .Select(g => new {
                                          Name = g.Key.ProcessName,
                                          ProcessId = g.Key.ProcessId,
                                          AvgVolume = g.Average(s => s.Volume),
                                          IsMuted = g.Any(s => s.IsMuted),
                                          SessionCount = g.Count()
                                      })
                                      .OrderByDescending(p => p.AvgVolume)
                                      .ToList();

            var maxNameLength = Math.Min(20, processGroups.Max(p => p.Name.Length));
            
            foreach (var process in processGroups)
            {
                var truncatedName = process.Name.Length > maxNameLength 
                    ? process.Name.Substring(0, maxNameLength - 3) + "..." 
                    : process.Name;
                var paddedName = truncatedName.PadRight(maxNameLength);
                
                var volumeBar = CreateVolumeBar(process.AvgVolume, 30);
                var muteIndicator = process.IsMuted ? "🔇" : "🔊";
                var sessionIndicator = process.SessionCount > 1 ? $"({process.SessionCount})" : "";
                
                Console.WriteLine($"{paddedName} │{volumeBar}│ {process.AvgVolume:P0} {muteIndicator} {sessionIndicator}");
            }
            
            Console.WriteLine();
            Console.WriteLine("Legend: 🔊 = Not Muted, 🔇 = Muted, (n) = Multiple Sessions");
        }

        public static void DisplayStatistics(List<AudioSession> defaultSessions, List<AudioSession> allDevicesSessions, List<AudioSession> captureSessions)
        {
            Console.WriteLine();
            Console.WriteLine("┌─ Discovery Statistics ─┐");
            Console.WriteLine("│");
            
            var stats = new[]
            {
                ("Default Config", defaultSessions.Count),
                ("All Devices", allDevicesSessions.Count),
                ("With Capture", captureSessions.Count)
            };
            
            var maxCount = stats.Max(s => s.Item2);
            var maxNameLength = stats.Max(s => s.Item1.Length);
            
            foreach (var (name, count) in stats)
            {
                var paddedName = name.PadRight(maxNameLength);
                var percentage = maxCount > 0 ? (double)count / maxCount : 0;
                var bar = CreateProgressBar(percentage, 25);
                Console.WriteLine($"├─ {paddedName} │{bar}│ {count} sessions");
            }
            
            Console.WriteLine("│");
            
            // Additional insights
            var additionalFromAllDevices = allDevicesSessions.Count - defaultSessions.Count;
            var additionalFromCapture = captureSessions.Count - defaultSessions.Count;
            
            if (additionalFromAllDevices > 0)
            {
                Console.WriteLine($"├─ ✅ All devices found {additionalFromAllDevices} additional sessions");
            }
            
            if (additionalFromCapture > 0)
            {
                Console.WriteLine($"├─ ✅ Capture devices found {additionalFromCapture} additional sessions");
            }
            
            if (additionalFromAllDevices == 0 && additionalFromCapture == 0)
            {
                Console.WriteLine($"├─ ℹ️  No additional sessions found with extended discovery");
            }
            
            Console.WriteLine("└─");
        }

        public static void ExportToHtml(List<AudioSession> defaultSessions, List<AudioSession> allDevicesSessions, List<AudioSession> captureSessions, string filename = "audio_sessions_report.html")
        {
            var html = GenerateHtmlReport(defaultSessions, allDevicesSessions, captureSessions);
            
            try
            {
                File.WriteAllText(filename, html);
                Console.WriteLine($"📊 HTML report exported to: {filename}");
                Console.WriteLine($"   Open in browser to view interactive charts");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to export HTML report: {ex.Message}");
            }
        }

        private static string CreateVolumeBar(double volume, int width)
        {
            var filledWidth = (int)(volume * width);
            var emptyWidth = width - filledWidth;
            
            var filled = new string('█', filledWidth);
            var empty = new string('░', emptyWidth);
            
            return filled + empty;
        }

        private static string CreateProgressBar(double percentage, int width)
        {
            var filledWidth = (int)(percentage * width);
            var emptyWidth = width - filledWidth;
            
            var filled = new string('█', filledWidth);
            var empty = new string('─', emptyWidth);
            
            return filled + empty;
        }

        private static string GenerateHtmlReport(List<AudioSession> defaultSessions, List<AudioSession> allDevicesSessions, List<AudioSession> captureSessions)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            
            var html = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Audio Sessions Discovery Report</title>
    <script src='https://cdn.jsdelivr.net/npm/chart.js'></script>
    <style>
        body {{ 
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; 
            margin: 20px; 
            background-color: #f5f5f5;
        }}
        .container {{ 
            max-width: 1200px; 
            margin: 0 auto; 
            background: white; 
            padding: 20px; 
            border-radius: 8px; 
            box-shadow: 0 2px 10px rgba(0,0,0,0.1);
        }}
        .header {{ 
            text-align: center; 
            margin-bottom: 30px; 
            color: #333;
        }}
        .chart-container {{ 
            margin: 20px 0; 
            height: 400px;
        }}
        .stats-grid {{ 
            display: grid; 
            grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); 
            gap: 20px; 
            margin: 20px 0;
        }}
        .stat-card {{ 
            background: #f8f9fa; 
            padding: 15px; 
            border-radius: 5px; 
            border-left: 4px solid #007bff;
        }}
        .stat-number {{ 
            font-size: 2em; 
            font-weight: bold; 
            color: #007bff;
        }}
        .stat-label {{ 
            color: #666; 
            margin-top: 5px;
        }}
        table {{ 
            width: 100%; 
            border-collapse: collapse; 
            margin-top: 20px;
        }}
        th, td {{ 
            padding: 10px; 
            text-align: left; 
            border-bottom: 1px solid #ddd;
        }}
        th {{ 
            background-color: #f8f9fa; 
            font-weight: 600;
        }}
        .volume-bar {{ 
            display: inline-block; 
            height: 20px; 
            background: linear-gradient(90deg, #28a745, #ffc107, #dc3545); 
            border-radius: 10px; 
            position: relative;
        }}
        .muted {{ color: #dc3545; }}
        .active {{ color: #28a745; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>🎵 Audio Sessions Discovery Report</h1>
            <p>Generated on {timestamp}</p>
        </div>

        <div class='stats-grid'>
            <div class='stat-card'>
                <div class='stat-number'>{defaultSessions.Count}</div>
                <div class='stat-label'>Default Config Sessions</div>
            </div>
            <div class='stat-card'>
                <div class='stat-number'>{allDevicesSessions.Count}</div>
                <div class='stat-label'>All Devices Sessions</div>
            </div>
            <div class='stat-card'>
                <div class='stat-number'>{captureSessions.Count}</div>
                <div class='stat-label'>With Capture Sessions</div>
            </div>
            <div class='stat-card'>
                <div class='stat-number'>{allDevicesSessions.Count - defaultSessions.Count}</div>
                <div class='stat-label'>Additional Sessions Found</div>
            </div>
        </div>

        <div class='chart-container'>
            <canvas id='discoveryChart'></canvas>
        </div>

        <div class='chart-container'>
            <canvas id='volumeChart'></canvas>
        </div>

        <h2>📋 Session Details</h2>
        {GenerateSessionTable(allDevicesSessions)}
    </div>

    <script>
        // Discovery Comparison Chart
        const discoveryCtx = document.getElementById('discoveryChart').getContext('2d');
        new Chart(discoveryCtx, {{
            type: 'bar',
            data: {{
                labels: ['Default Config', 'All Devices', 'With Capture'],
                datasets: [{{
                    label: 'Sessions Found',
                    data: [{defaultSessions.Count}, {allDevicesSessions.Count}, {captureSessions.Count}],
                    backgroundColor: ['#17a2b8', '#28a745', '#ffc107'],
                    borderWidth: 1
                }}]
            }},
            options: {{
                responsive: true,
                maintainAspectRatio: false,
                plugins: {{
                    title: {{
                        display: true,
                        text: 'Audio Session Discovery Comparison'
                    }}
                }},
                scales: {{
                    y: {{
                        beginAtZero: true,
                        ticks: {{
                            stepSize: 1
                        }}
                    }}
                }}
            }}
        }});

        // Volume Distribution Chart
        const volumeCtx = document.getElementById('volumeChart').getContext('2d');
        {GenerateVolumeChartData(allDevicesSessions)}
    </script>
</body>
</html>";

            return html;
        }

        private static string GenerateSessionTable(List<AudioSession> sessions)
        {
            if (!sessions.Any())
            {
                return "<p>No sessions found.</p>";
            }

            var tableBuilder = new StringBuilder();
            tableBuilder.AppendLine("<table>");
            tableBuilder.AppendLine("<thead>");
            tableBuilder.AppendLine("<tr>");
            tableBuilder.AppendLine("<th>Process Name</th>");
            tableBuilder.AppendLine("<th>PID</th>");
            tableBuilder.AppendLine("<th>Display Name</th>");
            tableBuilder.AppendLine("<th>Volume</th>");
            tableBuilder.AppendLine("<th>Status</th>");
            tableBuilder.AppendLine("<th>State</th>");
            tableBuilder.AppendLine("</tr>");
            tableBuilder.AppendLine("</thead>");
            tableBuilder.AppendLine("<tbody>");

            foreach (var session in sessions.OrderBy(s => s.ProcessName))
            {
                var volumeWidth = (int)(session.Volume * 100);
                var muteClass = session.IsMuted ? "muted" : "";
                var stateClass = session.SessionState == 1 ? "active" : "";
                var displayName = string.IsNullOrEmpty(session.DisplayName) ? "[No Display Name]" : session.DisplayName;

                tableBuilder.AppendLine("<tr>");
                tableBuilder.AppendLine($"<td><strong>{session.ProcessName}</strong></td>");
                tableBuilder.AppendLine($"<td>{session.ProcessId}</td>");
                tableBuilder.AppendLine($"<td>{displayName}</td>");
                tableBuilder.AppendLine($"<td><div class='volume-bar' style='width: {volumeWidth}px;'></div> {session.Volume:P0}</td>");
                tableBuilder.AppendLine($"<td class='{muteClass}'>{(session.IsMuted ? "🔇 Muted" : "🔊 Unmuted")}</td>");
                tableBuilder.AppendLine($"<td class='{stateClass}'>{GetStateText(session.SessionState)}</td>");
                tableBuilder.AppendLine("</tr>");
            }

            tableBuilder.AppendLine("</tbody>");
            tableBuilder.AppendLine("</table>");

            return tableBuilder.ToString();
        }

        private static string GenerateVolumeChartData(List<AudioSession> sessions)
        {
            var processGroups = sessions.GroupBy(s => s.ProcessName)
                                      .Select(g => new {
                                          Name = g.Key,
                                          AvgVolume = g.Average(s => s.Volume) * 100
                                      })
                                      .OrderByDescending(p => p.AvgVolume)
                                      .Take(10) // Top 10 processes
                                      .ToList();

            var labels = string.Join(", ", processGroups.Select(p => $"'{p.Name}'"));
            var data = string.Join(", ", processGroups.Select(p => p.AvgVolume.ToString("F1")));

            return $@"
        new Chart(volumeCtx, {{
            type: 'doughnut',
            data: {{
                labels: [{labels}],
                datasets: [{{
                    label: 'Average Volume %',
                    data: [{data}],
                    backgroundColor: [
                        '#FF6384', '#36A2EB', '#FFCE56', '#4BC0C0', '#9966FF',
                        '#FF9F40', '#FF6384', '#C9CBCF', '#4BC0C0', '#FF6384'
                    ]
                }}]
            }},
            options: {{
                responsive: true,
                maintainAspectRatio: false,
                plugins: {{
                    title: {{
                        display: true,
                        text: 'Volume Distribution by Process (Top 10)'
                    }},
                    legend: {{
                        position: 'right'
                    }}
                }}
            }}
        }});";
        }

        private static string GetStateText(int state)
        {
            return state switch
            {
                0 => "Inactive",
                1 => "Active",
                2 => "Expired",
                _ => "Unknown"
            };
        }

        public static void ShowVisualizationDemo()
        {
            Console.WriteLine("🎨 Audio Session Visualization Demo");
            Console.WriteLine();

            // Create sample data for demonstration
            var sampleSessions = new List<AudioSession>
            {
                new AudioSession { ProcessId = 1234, ProcessName = "Spotify", DisplayName = "Music Player", Volume = 0.75f, IsMuted = false, SessionState = 1 },
                new AudioSession { ProcessId = 5678, ProcessName = "Discord", DisplayName = "Voice Chat", Volume = 0.50f, IsMuted = false, SessionState = 1 },
                new AudioSession { ProcessId = 9999, ProcessName = "chrome", DisplayName = "YouTube", Volume = 0.90f, IsMuted = true, SessionState = 1 },
                new AudioSession { ProcessId = 1111, ProcessName = "steam", DisplayName = "Game Audio", Volume = 1.0f, IsMuted = false, SessionState = 1 },
                new AudioSession { ProcessId = 2222, ProcessName = "Teams", DisplayName = "Microsoft Teams", Volume = 0.60f, IsMuted = false, SessionState = 0 }
            };

            DisplayDeviceTree(sampleSessions, "Sample Audio Sessions");
            DisplayVolumeChart(sampleSessions, "Sample Volume Levels");
            
            Console.WriteLine();
            Console.WriteLine("This demonstrates how the visualizations will look with real audio session data!");
            Console.WriteLine("The actual test will show your system's real audio sessions.");
        }
    }
} 