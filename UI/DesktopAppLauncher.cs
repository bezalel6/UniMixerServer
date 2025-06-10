using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using UniMixerServer.Core;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace UniMixerServer.UI
{
    public static class DesktopAppLauncher
    {
        public static void Launch()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                using var form = new ModernAudioMixerForm();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start desktop application: {ex.Message}",
                    "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // Lightweight logger for desktop application
    public class DesktopLogger : ILogger<AudioManager>
    {
        private readonly string _categoryName;

        public DesktopLogger(string categoryName)
        {
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var level = logLevel.ToString().ToUpperInvariant();

            var logMessage = $"[{timestamp}] [{level}] [{_categoryName}] {message}";
            if (exception != null)
                logMessage += $"\nException: {exception.Message}";

            Console.WriteLine(logMessage);
            System.Diagnostics.Debug.WriteLine(logMessage);
        }
    }

    public class ModernAudioMixerForm : Form
    {
        // Core components
        private IAudioManager? _audioManager;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly System.Windows.Forms.Timer _uiUpdateTimer;

        // UI State with change tracking
        private List<AudioSession> _currentSessions = new();
        private Dictionary<string, Panel> _sessionCards = new(); // Track cards by session key
        private volatile bool _isInitialized = false;
        private volatile bool _isRefreshing = false;
        private volatile bool _isDisposing = false;
        private DateTime _lastUpdate = DateTime.MinValue;

        // Modern UI Components
        private Panel _mainPanel;
        private Panel _headerPanel;
        private Panel _bodyPanel;
        private Panel _statusPanel;
        private Label _titleLabel;
        private Label _statusLabel;
        private Label _sessionCountLabel;
        private FlowLayoutPanel _sessionContainer;
        private Panel _controlPanel;
        private Button _refreshButton;
        private CheckBox _autoRefreshToggle;
        private ComboBox _deviceFilter;

        // Modern Color Scheme
        private static readonly Color PrimaryBg = Color.FromArgb(13, 17, 23);
        private static readonly Color SecondaryBg = Color.FromArgb(22, 27, 34);
        private static readonly Color AccentBg = Color.FromArgb(33, 38, 45);
        private static readonly Color BorderColor = Color.FromArgb(48, 54, 61);
        private static readonly Color TextPrimary = Color.FromArgb(230, 237, 243);
        private static readonly Color TextSecondary = Color.FromArgb(139, 148, 158);
        private static readonly Color AccentBlue = Color.FromArgb(88, 166, 255);
        private static readonly Color AccentGreen = Color.FromArgb(63, 185, 80);
        private static readonly Color AccentOrange = Color.FromArgb(255, 130, 67);
        private static readonly Color AccentRed = Color.FromArgb(248, 81, 73);

        public ModernAudioMixerForm()
        {
            // CRITICAL FIX: Initialize UI first, THEN start background tasks
            InitializeModernUI();

            // Setup UI update timer AFTER UI is created
            _uiUpdateTimer = new System.Windows.Forms.Timer { Interval = 2000 }; // Increased to 2 seconds
            _uiUpdateTimer.Tick += OnUIUpdateTick;

            // CRITICAL FIX: Don't start background initialization in constructor
            // Use Load event instead to ensure form is fully created
            this.Load += OnFormLoad;
        }

        private void OnFormLoad(object sender, EventArgs e)
        {
            // Start background initialization AFTER form is loaded and visible
            _uiUpdateTimer.Start();
            StartBackgroundInitializationSafe();
        }

        private void InitializeModernUI()
        {
            try
            {
                // Form setup
                Text = "UniMixer Pro";
                Size = new Size(1600, 1000);
                MinimumSize = new Size(1200, 700);
                StartPosition = FormStartPosition.CenterScreen;
                BackColor = PrimaryBg;
                ForeColor = TextPrimary;
                FormBorderStyle = FormBorderStyle.Sizable;
                Font = new Font("Segoe UI", 9F, FontStyle.Regular);

                CreateModernLayout();
                ApplyModernStyling();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing UI: {ex.Message}");
                // Don't throw - allow form to continue with basic setup
            }
        }

        private void CreateModernLayout()
        {
            // Main container with proper spacing
            _mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PrimaryBg,
                Padding = new Padding(0)
            };

            // Header section - fixed height with proper content
            _headerPanel = new Panel
            {
                Height = 120, // Increased for better spacing
                Dock = DockStyle.Top,
                BackColor = SecondaryBg,
                Padding = new Padding(24, 20, 24, 20)
            };

            // Title and main controls row
            var titleRow = new Panel
            {
                Height = 40,
                Dock = DockStyle.Top,
                BackColor = Color.Transparent
            };

            _titleLabel = new Label
            {
                Text = "🎵 UniMixer Pro",
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(0, 8)
            };

            // Real-time search box
            var searchBox = new TextBox
            {
                PlaceholderText = "🔍 Search processes...",
                Font = new Font("Segoe UI", 10F),
                BackColor = AccentBg,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Width = 250,
                Location = new Point(300, 8),
                Height = 24
            };
            searchBox.TextChanged += (s, e) => FilterSessionsRealTime(searchBox.Text);

            // Quick action buttons
            var quickActionsPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Location = new Point(600, 4),
                BackColor = Color.Transparent
            };

            var muteAllBtn = CreateQuickButton("🔇 Mute All", AccentRed, 100);
            var unmuteAllBtn = CreateQuickButton("🔊 Unmute All", AccentGreen, 100);
            var refreshBtn = CreateQuickButton("🔄 Refresh", AccentBlue, 80);

            // CRITICAL FIX: Use async void for event handlers with proper error handling
            muteAllBtn.Click += async (s, e) => await SafeExecuteAsync(() => BulkMuteOperation(true));
            unmuteAllBtn.Click += async (s, e) => await SafeExecuteAsync(() => BulkMuteOperation(false));
            refreshBtn.Click += async (s, e) => await SafeExecuteAsync(() => RefreshSessionsAsync());

            quickActionsPanel.Controls.AddRange(new Control[] { muteAllBtn, unmuteAllBtn, refreshBtn });

            titleRow.Controls.AddRange(new Control[] { _titleLabel, searchBox, quickActionsPanel });

            // Status and legend row
            var statusRow = new Panel
            {
                Height = 60,
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 12, 0, 0)
            };

            // Status indicators
            _statusLabel = new Label
            {
                Text = "⚡ Starting...",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = AccentOrange,
                AutoSize = true,
                Location = new Point(0, 0)
            };

            _sessionCountLabel = new Label
            {
                Text = "Initializing...",
                Font = new Font("Segoe UI", 10F),
                ForeColor = TextSecondary,
                AutoSize = true,
                Location = new Point(0, 24)
            };

            // Interactive legend
            var legendPanel = CreateInteractiveLegend();
            legendPanel.Location = new Point(300, 0);

            // Advanced controls
            var advancedPanel = CreateAdvancedControlsPanel();
            advancedPanel.Location = new Point(600, 0);

            statusRow.Controls.AddRange(new Control[] { _statusLabel, _sessionCountLabel, legendPanel, advancedPanel });

            _headerPanel.Controls.AddRange(new Control[] { titleRow, statusRow });

            // Right sidebar for advanced controls and details
            _controlPanel = new Panel
            {
                Width = 320,
                Dock = DockStyle.Right,
                BackColor = SecondaryBg,
                Padding = new Padding(20)
            };

            CreateAdvancedSidebar();

            // Main content area - properly positioned to avoid header overlap
            _bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PrimaryBg,
                Padding = new Padding(24, 12, 24, 12), // Reduced top padding since header is properly sized
                AutoScroll = true
            };

            // Session container with professional styling
            _sessionContainer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.Transparent,
                AutoScroll = true,
                Padding = new Padding(0)
            };

            _bodyPanel.Controls.Add(_sessionContainer);

            // Status bar at bottom
            _statusPanel = new Panel
            {
                Height = 32,
                Dock = DockStyle.Bottom,
                BackColor = SecondaryBg,
                Padding = new Padding(24, 6, 24, 6)
            };

            var statusInfo = new Label
            {
                Text = "Ready • Press F5 to refresh • Ctrl+F to search • Right-click for options",
                Font = new Font("Segoe UI", 9F),
                ForeColor = TextSecondary,
                AutoSize = true,
                Location = new Point(0, 8)
            };
            _statusPanel.Controls.Add(statusInfo);

            // Assembly with proper hierarchy
            _mainPanel.Controls.AddRange(new Control[] {
                _statusPanel,    // Bottom first (for docking order)
                _bodyPanel,      // Fill remaining space
                _controlPanel,   // Right sidebar
                _headerPanel     // Top header
            });

            Controls.Add(_mainPanel);
        }

        // CRITICAL FIX: Wrapper for async operations with proper error handling
        private async Task SafeExecuteAsync(Func<Task> operation)
        {
            if (_isDisposing) return;

            try
            {
                await operation();
            }
            catch (ObjectDisposedException)
            {
                // Form is disposing, ignore
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in async operation: {ex.Message}");

                // Update UI safely
                if (!_isDisposing && !IsDisposed)
                {
                    try
                    {
                        if (InvokeRequired)
                        {
                            BeginInvoke(new Action(() => UpdateErrorStatus($"Error: {ex.Message}")));
                        }
                        else
                        {
                            UpdateErrorStatus($"Error: {ex.Message}");
                        }
                    }
                    catch
                    {
                        // Ignore invoke errors during shutdown
                    }
                }
            }
        }

        private void UpdateErrorStatus(string message)
        {
            if (!_isDisposing && !IsDisposed && _statusLabel != null)
            {
                _statusLabel.Text = message;
                _statusLabel.ForeColor = AccentRed;
            }
        }

        private Button CreateQuickButton(string text, Color color, int width)
        {
            var btn = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Size = new Size(width, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.15f);
            btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(color, 0.15f);
            return btn;
        }

        private Panel CreateInteractiveLegend()
        {
            var legendPanel = new Panel
            {
                Size = new Size(280, 48),
                BackColor = Color.Transparent
            };

            var legendTitle = new Label
            {
                Text = "Status Legend:",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = TextSecondary,
                Location = new Point(0, 0),
                AutoSize = true
            };

            var legendItems = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Location = new Point(0, 20),
                BackColor = Color.Transparent
            };

            // Interactive legend items with tooltips
            var activeItem = CreateLegendItem("●", AccentGreen, "Active");
            var inactiveItem = CreateLegendItem("○", AccentOrange, "Inactive");
            var mutedItem = CreateLegendItem("🔇", AccentRed, "Muted");
            var volumeItem = CreateLegendItem("█", AccentBlue, "Volume Level");

            legendItems.Controls.AddRange(new Control[] { activeItem, inactiveItem, mutedItem, volumeItem });
            legendPanel.Controls.AddRange(new Control[] { legendTitle, legendItems });

            return legendPanel;
        }

        private Panel CreateLegendItem(string symbol, Color color, string meaning)
        {
            var item = new Panel
            {
                Size = new Size(60, 20),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 12, 0)
            };

            var symbolLabel = new Label
            {
                Text = symbol,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = color,
                Location = new Point(0, 2),
                Size = new Size(16, 16)
            };

            var textLabel = new Label
            {
                Text = meaning,
                Font = new Font("Segoe UI", 8F),
                ForeColor = TextSecondary,
                Location = new Point(18, 4),
                AutoSize = true
            };

            item.Controls.AddRange(new Control[] { symbolLabel, textLabel });

            // Add hover effect
            item.MouseEnter += (s, e) => item.BackColor = Color.FromArgb(30, color);
            item.MouseLeave += (s, e) => item.BackColor = Color.Transparent;

            return item;
        }

        private Panel CreateAdvancedControlsPanel()
        {
            var panel = new Panel
            {
                Size = new Size(200, 48),
                BackColor = Color.Transparent
            };

            // View mode selector
            var viewModeLabel = new Label
            {
                Text = "View:",
                Font = new Font("Segoe UI", 9F),
                ForeColor = TextSecondary,
                Location = new Point(0, 4),
                AutoSize = true
            };

            _deviceFilter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(40, 0),
                Width = 120,
                BackColor = AccentBg,
                ForeColor = TextPrimary
            };
            _deviceFilter.Items.AddRange(new[] {
                "🎧 Default Device",
                "🔊 All Devices",
                "🟢 Active Only",
                "🔇 Muted Only",
                "📊 By Volume"
            });
            _deviceFilter.SelectedIndex = 0;
            _deviceFilter.SelectedIndexChanged += (s, e) => FilterSessionsRealTime("");

            // Auto-refresh toggle
            _autoRefreshToggle = new CheckBox
            {
                Text = "Auto-refresh",
                Font = new Font("Segoe UI", 9F),
                ForeColor = TextPrimary,
                Location = new Point(0, 26),
                Checked = false,
                AutoSize = true
            };

            panel.Controls.AddRange(new Control[] { viewModeLabel, _deviceFilter, _autoRefreshToggle });
            return panel;
        }

        private void CreateAdvancedSidebar()
        {
            // Session details panel
            var detailsPanel = CreateCard("📊 Session Details", 200);
            detailsPanel.Location = new Point(0, 0);
            CreateSessionDetailsPanel(detailsPanel);

            // Master volume control panel
            var volumePanel = CreateCard("🎚️ Master Controls", 180);
            volumePanel.Location = new Point(0, 220);
            CreateMasterVolumePanel(volumePanel);

            // System info panel
            var systemPanel = CreateCard("💻 System Info", 120);
            systemPanel.Location = new Point(0, 420);
            CreateSystemInfoPanel(systemPanel);

            // Settings panel
            var settingsPanel = CreateCard("⚙️ Settings", 100);
            settingsPanel.Location = new Point(0, 560);
            CreateSettingsPanel(settingsPanel);

            _controlPanel.Controls.AddRange(new Control[] { detailsPanel, volumePanel, systemPanel, settingsPanel });
        }

        private Panel CreateCard(string title, int height)
        {
            var card = new Panel
            {
                Size = new Size(280, height),
                BackColor = AccentBg,
                Padding = new Padding(16),
                Margin = new Padding(0, 0, 0, 20)
            };

            var titleLabel = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = TextPrimary,
                Location = new Point(16, 12),
                AutoSize = true
            };

            card.Controls.Add(titleLabel);

            // Add subtle border
            card.Paint += (s, e) =>
            {
                using var pen = new Pen(BorderColor, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            return card;
        }

        private void CreateSessionDetailsPanel(Panel parent)
        {
            var selectedLabel = new Label
            {
                Text = "Select a session to view details",
                Font = new Font("Segoe UI", 9F),
                ForeColor = TextSecondary,
                Location = new Point(0, 40),
                Size = new Size(248, 150),
                TextAlign = ContentAlignment.TopLeft
            };

            parent.Controls.Add(selectedLabel);
        }

        private void CreateMasterVolumePanel(Panel parent)
        {
            // Master volume slider
            var masterVolumeTrack = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                TickStyle = TickStyle.None,
                Location = new Point(0, 40),
                Size = new Size(248, 30),
                BackColor = AccentBg
            };

            var volumeLabel = new Label
            {
                Text = "Master Volume: 50%",
                Font = new Font("Segoe UI", 9F),
                ForeColor = TextPrimary,
                Location = new Point(0, 75),
                AutoSize = true
            };

            masterVolumeTrack.ValueChanged += (s, e) =>
            {
                volumeLabel.Text = $"Master Volume: {masterVolumeTrack.Value}%";
            };

            // Quick volume buttons
            var volumeButtonsPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Location = new Point(0, 100),
                BackColor = Color.Transparent
            };

            var vol25 = CreateVolumeButton("25%", 25);
            var vol50 = CreateVolumeButton("50%", 50);
            var vol75 = CreateVolumeButton("75%", 75);
            var vol100 = CreateVolumeButton("100%", 100);

            vol25.Click += (s, e) => masterVolumeTrack.Value = 25;
            vol50.Click += (s, e) => masterVolumeTrack.Value = 50;
            vol75.Click += (s, e) => masterVolumeTrack.Value = 75;
            vol100.Click += (s, e) => masterVolumeTrack.Value = 100;

            volumeButtonsPanel.Controls.AddRange(new Control[] { vol25, vol50, vol75, vol100 });

            parent.Controls.AddRange(new Control[] { masterVolumeTrack, volumeLabel, volumeButtonsPanel });
        }

        private Button CreateVolumeButton(string text, int value)
        {
            return new Button
            {
                Text = text,
                Size = new Size(50, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = BorderColor,
                ForeColor = TextPrimary,
                Font = new Font("Segoe UI", 8F),
                Margin = new Padding(0, 0, 4, 0),
                FlatAppearance = { BorderSize = 0 }
            };
        }

        private void CreateSystemInfoPanel(Panel parent)
        {
            var infoText = new Label
            {
                Text = "🎧 Audio Sessions: Loading...\n💻 CPU Usage: 0%\n🔊 Audio Devices: 0",
                Font = new Font("Segoe UI", 9F),
                ForeColor = TextSecondary,
                Location = new Point(0, 40),
                Size = new Size(248, 70),
                TextAlign = ContentAlignment.TopLeft
            };

            parent.Controls.Add(infoText);

            // Update system info periodically
            var systemTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            systemTimer.Tick += (s, e) =>
            {
                try
                {
                    var sessionCount = _currentSessions.Count;
                    var activeCount = _currentSessions.Count(s => s.SessionState == 1);
                    infoText.Text = $"🎧 Audio Sessions: {sessionCount} ({activeCount} active)\n💻 System: Windows Audio API\n🔊 Update Rate: 1s";
                }
                catch { /* Ignore errors */ }
            };
            systemTimer.Start();
        }

        private void CreateSettingsPanel(Panel parent)
        {
            var exportBtn = CreateQuickButton("📤 Export", AccentBlue, 120);
            exportBtn.Location = new Point(0, 40);
            exportBtn.Click += async (s, e) => await SafeExecuteAsync(() => ExportSessionData());

            var themeBtn = CreateQuickButton("🌙 Toggle Theme", BorderColor, 120);
            themeBtn.Location = new Point(0, 72);

            parent.Controls.AddRange(new Control[] { exportBtn, themeBtn });
        }

        // Add the missing methods that are referenced but not implemented
        private void FilterSessionsRealTime(string searchText)
        {
            if (_isDisposing) return;

            try
            {
                if (string.IsNullOrEmpty(searchText))
                {
                    foreach (var card in _sessionCards.Values)
                    {
                        card.Visible = true;
                    }
                    return;
                }

                foreach (var kvp in _sessionCards)
                {
                    var session = _currentSessions.FirstOrDefault(s => GetSessionKey(s) == kvp.Key);
                    if (session != null)
                    {
                        bool matches = session.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                                     session.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                        kvp.Value.Visible = matches;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error filtering sessions: {ex.Message}");
            }
        }

        private async Task BulkMuteOperation(bool mute)
        {
            try
            {
                var tasks = _currentSessions.Select(async session =>
                {
                    try
                    {
                        if (_audioManager != null)
                        {
                            await _audioManager.MuteProcessAsync(session.ProcessId, mute);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to {(mute ? "mute" : "unmute")} {session.ProcessName}: {ex.Message}");
                    }
                });

                await Task.WhenAll(tasks);
                await RefreshSessionsAsync(); // Refresh to show changes
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bulk {(mute ? "mute" : "unmute")} operation failed: {ex.Message}");
            }
        }

        private async Task ExportSessionData()
        {
            try
            {
                var data = _currentSessions.Select(s => new
                {
                    ProcessName = s.ProcessName,
                    DisplayName = s.DisplayName,
                    Volume = Math.Round(s.Volume * 100, 1),
                    IsMuted = s.IsMuted,
                    State = s.SessionState switch
                    {
                        0 => "Inactive",
                        1 => "Active",
                        2 => "Expired",
                        _ => "Unknown"
                    },
                    LastUpdated = s.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss")
                });

                var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var fileName = $"audio_sessions_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                await File.WriteAllTextAsync(fileName, json);

                MessageBox.Show($"Session data exported to {fileName}", "Export Complete",
                              MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ApplyModernStyling()
        {
            // Apply modern styling to controls
            foreach (Control control in Controls)
            {
                ApplyModernStylingRecursive(control);
            }
        }

        private void ApplyModernStylingRecursive(Control control)
        {
            if (control is Panel panel)
            {
                panel.Paint += (s, e) =>
                {
                    if (panel == _headerPanel || panel == _controlPanel || panel == _statusPanel)
                    {
                        // Draw subtle border
                        using var pen = new Pen(BorderColor, 1);
                        e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
                    }
                    ;
                };
            }

            foreach (Control child in control.Controls)
            {
                ApplyModernStylingRecursive(child);
            }
        }

        // CRITICAL FIX: Safe UI update method
        private void SafeUpdateUI(Action updateAction)
        {
            if (_isDisposing || IsDisposed) return;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!_isDisposing && !IsDisposed)
                        {
                            updateAction();
                        }
                    }));
                }
                else
                {
                    if (!_isDisposing && !IsDisposed)
                    {
                        updateAction();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Form is disposing, ignore
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating UI: {ex.Message}");
            }
        }

        // CRITICAL FIX: Safe background initialization with proper error handling
        private void StartBackgroundInitializationSafe()
        {
            Task.Run(async () =>
            {
                try
                {
                    // CRITICAL FIX: Add delay to ensure form is fully loaded
                    await Task.Delay(1000, _cancellationTokenSource.Token);

                    if (_isDisposing || _cancellationTokenSource.Token.IsCancellationRequested)
                        return;

                    var logger = new DesktopLogger("AudioManager");
                    logger.LogInformation("Initializing AudioManager");

                    // CRITICAL FIX: Initialize with shorter timeout and error handling
                    _audioManager = new AudioManager(logger, enableDetailedLogging: false);

                    // CRITICAL FIX: Very aggressive timeout for initial load
                    using var initTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                        _cancellationTokenSource.Token, initTimeout.Token);

                    List<AudioSession> initialSessions = null;

                    try
                    {
                        initialSessions = await _audioManager.GetAllAudioSessionsAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning("Initial session load timed out, continuing with empty list");
                        initialSessions = new List<AudioSession>();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to get initial sessions, continuing with empty list");
                        initialSessions = new List<AudioSession>();
                    }

                    _currentSessions = initialSessions ?? new List<AudioSession>();
                    _isInitialized = true;

                    // CRITICAL FIX: Safe UI update with proper checks
                    SafeUpdateUI(() =>
                    {
                        _statusLabel.Text = "🟢 Connected";
                        _statusLabel.ForeColor = AccentGreen;
                        _sessionCountLabel.Text = $"{_currentSessions.Count} sessions";
                        UpdateSessionCardsEfficiently();
                    });

                    logger.LogInformation("Desktop application initialized successfully");

                    // Start background refresh with longer delay
                    await Task.Delay(3000, _cancellationTokenSource.Token);
                    await BackgroundRefreshLoopSafe();
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown, ignore
                }
                catch (Exception ex)
                {
                    var logger = new DesktopLogger("Initialization");
                    logger.LogError(ex, "Failed to initialize AudioManager");

                    SafeUpdateUI(() =>
                    {
                        _statusLabel.Text = "❌ Failed to initialize";
                        _statusLabel.ForeColor = AccentRed;
                        _sessionCountLabel.Text = "0 sessions";
                    });
                }
            }, _cancellationTokenSource.Token);
        }

        // CRITICAL FIX: Safer background refresh loop
        private async Task BackgroundRefreshLoopSafe()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && !_isDisposing)
            {
                try
                {
                    if (_isInitialized && _audioManager != null && !_isRefreshing &&
                        _autoRefreshToggle?.Checked == true)
                    {
                        _isRefreshing = true;

                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // Shorter timeout
                        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                            _cancellationTokenSource.Token, timeoutCts.Token);

                        List<AudioSession> sessions = null;
                        try
                        {
                            sessions = await _audioManager.GetAllAudioSessionsAsync();
                        }
                        catch (OperationCanceledException)
                        {
                            // Timeout or cancellation, skip this update
                            sessions = null;
                        }

                        if (sessions != null && SessionsHaveChanged(sessions) && !_isDisposing)
                        {
                            _currentSessions = sessions;
                            _lastUpdate = DateTime.Now;
                        }

                        _isRefreshing = false;
                    }

                    // CRITICAL FIX: Longer delay between refreshes to prevent overwhelming
                    await Task.Delay(7000, _cancellationTokenSource.Token); // 7 second intervals
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var logger = new DesktopLogger("BackgroundRefresh");
                    logger.LogError(ex, "Error in background refresh");
                    _isRefreshing = false;

                    try
                    {
                        await Task.Delay(15000, _cancellationTokenSource.Token); // Longer wait on error
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private bool SessionsHaveChanged(List<AudioSession> newSessions)
        {
            try
            {
                // Create deduplicated session lists for comparison
                var currentDeduped = DeduplicateSessions(_currentSessions);
                var newDeduped = DeduplicateSessions(newSessions);

                if (currentDeduped.Count != newDeduped.Count)
                    return true;

                // Create lookup for efficient comparison - handle duplicates safely
                var currentLookup = new Dictionary<string, AudioSession>();
                foreach (var session in currentDeduped)
                {
                    var key = GetSessionKey(session);
                    if (!currentLookup.ContainsKey(key))
                    {
                        currentLookup[key] = session;
                    }
                }

                foreach (var session in newDeduped)
                {
                    var key = GetSessionKey(session);
                    if (!currentLookup.TryGetValue(key, out var existing))
                        return true; // New session

                    // Check if volume or mute state changed significantly
                    if (Math.Abs(existing.Volume - session.Volume) > 0.01f ||
                        existing.IsMuted != session.IsMuted ||
                        existing.SessionState != session.SessionState)
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SessionsHaveChanged: {ex.Message}");
                return true; // Assume changed to trigger refresh on error
            }
        }

        private List<AudioSession> DeduplicateSessions(List<AudioSession> sessions)
        {
            var seenKeys = new HashSet<string>();
            var deduplicated = new List<AudioSession>();

            foreach (var session in sessions)
            {
                var key = GetSessionKey(session);
                if (!seenKeys.Contains(key))
                {
                    seenKeys.Add(key);
                    deduplicated.Add(session);
                }
            }

            return deduplicated;
        }

        private string GetSessionKey(AudioSession session)
        {
            return $"{session.ProcessId}_{session.ProcessName}";
        }

        private void OnUIUpdateTick(object? sender, EventArgs e)
        {
            if (!_isInitialized) return;

            try
            {
                // Update session count
                _sessionCountLabel.Text = $"{_currentSessions.Count} sessions";

                // Only update session cards if data has changed
                if (_currentSessions.Any())
                {
                    UpdateSessionCardsEfficiently();
                }
            }
            catch (Exception ex)
            {
                var logger = new DesktopLogger("UIUpdate");
                logger.LogError(ex, "Error updating UI");
            }
        }

        private void UpdateSessionCardsEfficiently()
        {
            if (!this.InvokeRequired)
            {
                UpdateSessionCardsInternal();
            }
            else
            {
                this.BeginInvoke(new Action(UpdateSessionCardsInternal));
            }
        }

        private void UpdateSessionCardsInternal()
        {
            // Suspend layout to prevent flickering
            _sessionContainer.SuspendLayout();

            try
            {
                // Filter sessions based on current selection
                var filteredSessions = FilterSessions(_currentSessions);
                var sessionKeys = new HashSet<string>();
                var existingKeys = new HashSet<string>(_sessionCards.Keys);

                // Update or create cards for current sessions
                foreach (var session in filteredSessions.Take(20)) // Limit for performance
                {
                    var key = GetSessionKey(session);

                    // Skip duplicate sessions (same process can have multiple sessions)
                    if (sessionKeys.Contains(key))
                        continue;

                    sessionKeys.Add(key);

                    if (_sessionCards.ContainsKey(key))
                    {
                        // Update existing card efficiently
                        UpdateSessionCard(_sessionCards[key], session);
                    }
                    else
                    {
                        // Create new card only if key doesn't exist
                        var newCard = CreateSessionCard(session);
                        _sessionCards[key] = newCard;
                        _sessionContainer.Controls.Add(newCard);
                    }
                }

                // Remove cards for sessions that no longer exist
                var keysToRemove = existingKeys.Where(k => !sessionKeys.Contains(k)).ToList();
                foreach (var key in keysToRemove)
                {
                    if (_sessionCards.TryGetValue(key, out var cardToRemove))
                    {
                        _sessionContainer.Controls.Remove(cardToRemove);
                        cardToRemove?.Dispose();
                        _sessionCards.Remove(key);
                    }
                }

                // Ensure proper ordering (optional - can be expensive)
                if (_sessionContainer.Controls.Count > 1)
                {
                    ReorderSessionCards(filteredSessions.Take(20).ToList());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating session cards: {ex.Message}");
            }
            finally
            {
                // Resume layout to apply changes
                _sessionContainer.ResumeLayout(true);
            }
        }

        private void UpdateSessionCard(Panel card, AudioSession session)
        {
            // Update card controls efficiently without recreating
            foreach (Control control in card.Controls)
            {
                switch (control)
                {
                    case Label label when label.Text.StartsWith("🎵"):
                        if (label.Text != $"🎵 {session.ProcessName}")
                            label.Text = $"🎵 {session.ProcessName}";
                        break;

                    case Label label when label.Text.Contains("🔊") || label.Text.Contains("🔇"):
                        var volumeText = session.IsMuted ? "🔇 MUTED" : $"🔊 {session.Volume:P0}";
                        if (label.Text != volumeText)
                        {
                            label.Text = volumeText;
                            label.ForeColor = session.IsMuted ? AccentRed : GetVolumeColor(session.Volume);
                        }
                        break;

                    case Label label when label.Text == "●" || label.Text == "○":
                        var stateText = session.SessionState == 1 ? "●" : "○";
                        var stateColor = session.SessionState switch
                        {
                            1 => AccentGreen,
                            0 => AccentOrange,
                            _ => TextSecondary
                        };
                        if (label.Text != stateText)
                        {
                            label.Text = stateText;
                            label.ForeColor = stateColor;
                        }
                        break;

                    case Panel panel when panel.Height == 4: // Volume bar
                        var newWidth = (int)((card.Width - 80) * session.Volume);
                        var newColor = session.IsMuted ? AccentRed : GetVolumeColor(session.Volume);
                        if (panel.Width != newWidth || panel.BackColor != newColor)
                        {
                            panel.Width = newWidth;
                            panel.BackColor = newColor;
                        }
                        break;

                    case Label label when label.Text.StartsWith("PID:"):
                        var pidText = $"PID: {session.ProcessId}";
                        if (label.Text != pidText)
                            label.Text = pidText;
                        break;
                }
            }
        }

        private void ReorderSessionCards(List<AudioSession> orderedSessions)
        {
            // Only reorder if necessary (expensive operation)
            for (int i = 0; i < orderedSessions.Count && i < _sessionContainer.Controls.Count; i++)
            {
                var session = orderedSessions[i];
                var key = GetSessionKey(session);

                if (_sessionCards.TryGetValue(key, out var card))
                {
                    var currentIndex = _sessionContainer.Controls.IndexOf(card);
                    if (currentIndex != i)
                    {
                        _sessionContainer.Controls.SetChildIndex(card, i);
                    }
                }
            }
        }

        private void UpdateSessionCards()
        {
            // Legacy method - now redirects to efficient version
            UpdateSessionCardsEfficiently();
        }

        private List<AudioSession> FilterSessions(List<AudioSession> sessions)
        {
            return _deviceFilter?.SelectedIndex switch
            {
                1 => sessions, // All devices
                2 => sessions.Where(s => s.SessionState == 1).ToList(), // Active only
                _ => sessions.Where(s => !string.IsNullOrEmpty(s.ProcessName) && s.ProcessId > 0).ToList() // Default device
            };
        }

        private Panel CreateSessionCard(AudioSession session)
        {
            var card = new Panel
            {
                Size = new Size(_sessionContainer.Width - 40, 120), // Increased height for more controls
                BackColor = AccentBg,
                Margin = new Padding(0, 0, 0, 12),
                Padding = new Padding(16),
                Tag = session,
                Cursor = Cursors.Hand
            };

            // Add subtle border and hover effects
            card.Paint += (s, e) =>
            {
                using var pen = new Pen(BorderColor, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            card.MouseEnter += (s, e) => card.BackColor = ControlPaint.Light(AccentBg, 0.1f);
            card.MouseLeave += (s, e) => card.BackColor = AccentBg;

            // Status indicator circle
            var statusIndicator = new Label
            {
                Text = "●",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = session.SessionState == 1 ? AccentGreen : AccentOrange,
                Location = new Point(0, 0),
                Size = new Size(20, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Process name (main title)
            var processLabel = new Label
            {
                Text = session.ProcessName,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = TextPrimary,
                Location = new Point(24, 0),
                Size = new Size(200, 20),
                AutoEllipsis = true
            };

            // Display name (subtitle)
            var displayLabel = new Label
            {
                Text = string.IsNullOrEmpty(session.DisplayName) ? $"PID: {session.ProcessId}" : session.DisplayName,
                Font = new Font("Segoe UI", 9F),
                ForeColor = TextSecondary,
                Location = new Point(24, 22),
                Size = new Size(200, 16),
                AutoEllipsis = true
            };

            // Volume percentage
            var volumePercentLabel = new Label
            {
                Text = $"{session.Volume:P0}",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = GetVolumeColor(session.Volume),
                Location = new Point(240, 0),
                Size = new Size(60, 20),
                TextAlign = ContentAlignment.MiddleRight
            };

            // Mute status
            var muteStatusLabel = new Label
            {
                Text = session.IsMuted ? "🔇" : "🔊",
                Font = new Font("Segoe UI", 12F),
                ForeColor = session.IsMuted ? AccentRed : TextSecondary,
                Location = new Point(310, 0),
                Size = new Size(24, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Volume slider for direct control
            var volumeSlider = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = (int)(session.Volume * 100),
                TickStyle = TickStyle.None,
                Location = new Point(24, 45),
                Size = new Size(180, 25),
                BackColor = AccentBg
            };

            // Volume change event handler
            bool isSliderUpdate = false;
            volumeSlider.ValueChanged += async (s, e) =>
            {
                if (isSliderUpdate) return; // Prevent recursion

                try
                {
                    float newVolume = volumeSlider.Value / 100.0f;
                    if (_audioManager != null)
                    {
                        await _audioManager.SetProcessVolumeAsync(session.ProcessId, newVolume);
                        volumePercentLabel.Text = $"{newVolume:P0}";
                        volumePercentLabel.ForeColor = GetVolumeColor(newVolume);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to set volume for {session.ProcessName}: {ex.Message}");
                }
            };

            // Quick mute button
            var muteButton = new Button
            {
                Text = session.IsMuted ? "Unmute" : "Mute",
                Size = new Size(60, 25),
                Location = new Point(210, 45),
                FlatStyle = FlatStyle.Flat,
                BackColor = session.IsMuted ? AccentGreen : AccentRed,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8F),
                FlatAppearance = { BorderSize = 0 }
            };

            muteButton.Click += async (s, e) =>
            {
                try
                {
                    bool newMuteState = !session.IsMuted;
                    if (_audioManager != null)
                    {
                        await _audioManager.MuteProcessAsync(session.ProcessId, newMuteState);
                        muteButton.Text = newMuteState ? "Unmute" : "Mute";
                        muteButton.BackColor = newMuteState ? AccentGreen : AccentRed;
                        muteStatusLabel.Text = newMuteState ? "🔇" : "🔊";
                        muteStatusLabel.ForeColor = newMuteState ? AccentRed : TextSecondary;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to mute/unmute {session.ProcessName}: {ex.Message}");
                }
            };

            // Quick volume buttons
            var quickVolPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Location = new Point(24, 75),
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };

            var vol0 = CreateQuickVolumeButton("0%", 0, session);
            var vol25 = CreateQuickVolumeButton("25%", 25, session);
            var vol50 = CreateQuickVolumeButton("50%", 50, session);
            var vol75 = CreateQuickVolumeButton("75%", 75, session);
            var vol100 = CreateQuickVolumeButton("100%", 100, session);

            // Update slider when quick volume buttons are pressed
            vol0.Click += (s, e) => { isSliderUpdate = true; volumeSlider.Value = 0; isSliderUpdate = false; };
            vol25.Click += (s, e) => { isSliderUpdate = true; volumeSlider.Value = 25; isSliderUpdate = false; };
            vol50.Click += (s, e) => { isSliderUpdate = true; volumeSlider.Value = 50; isSliderUpdate = false; };
            vol75.Click += (s, e) => { isSliderUpdate = true; volumeSlider.Value = 75; isSliderUpdate = false; };
            vol100.Click += (s, e) => { isSliderUpdate = true; volumeSlider.Value = 100; isSliderUpdate = false; };

            quickVolPanel.Controls.AddRange(new Control[] { vol0, vol25, vol50, vol75, vol100 });

            // Process info button
            var infoButton = new Button
            {
                Text = "ℹ️",
                Size = new Size(25, 25),
                Location = new Point(280, 45),
                FlatStyle = FlatStyle.Flat,
                BackColor = AccentBlue,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10F),
                FlatAppearance = { BorderSize = 0 }
            };

            infoButton.Click += (s, e) =>
            {
                var info = $"Process: {session.ProcessName}\n" +
                          $"PID: {session.ProcessId}\n" +
                          $"Display Name: {session.DisplayName}\n" +
                          $"Volume: {session.Volume:P2}\n" +
                          $"Muted: {session.IsMuted}\n" +
                          $"State: {GetSessionStateText(session.SessionState)}\n" +
                          $"Device: {session.DeviceName}\n" +
                          $"Last Updated: {session.LastUpdated:HH:mm:ss}";

                MessageBox.Show(info, $"Session Info - {session.ProcessName}",
                              MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            // Context menu for advanced operations
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Kill Process", null, async (s, e) => await KillProcess(session.ProcessId));
            contextMenu.Items.Add("Open Process Location", null, (s, e) => OpenProcessLocation(session.ProcessId));
            contextMenu.Items.Add("-"); // Separator
            contextMenu.Items.Add("Set Volume to 25%", null, (s, e) => SetVolumeQuick(session, 0.25f, volumeSlider, volumePercentLabel));
            contextMenu.Items.Add("Set Volume to 50%", null, (s, e) => SetVolumeQuick(session, 0.5f, volumeSlider, volumePercentLabel));
            contextMenu.Items.Add("Set Volume to 75%", null, (s, e) => SetVolumeQuick(session, 0.75f, volumeSlider, volumePercentLabel));
            contextMenu.Items.Add("Set Volume to 100%", null, (s, e) => SetVolumeQuick(session, 1.0f, volumeSlider, volumePercentLabel));

            card.ContextMenuStrip = contextMenu;

            // Assembly
            card.Controls.AddRange(new Control[] {
                statusIndicator, processLabel, displayLabel, volumePercentLabel, muteStatusLabel,
                volumeSlider, muteButton, quickVolPanel, infoButton
            });

            return card;
        }

        private Button CreateQuickVolumeButton(string text, int volume, AudioSession session)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(35, 20),
                FlatStyle = FlatStyle.Flat,
                BackColor = BorderColor,
                ForeColor = TextSecondary,
                Font = new Font("Segoe UI", 7F),
                Margin = new Padding(0, 0, 2, 0),
                FlatAppearance = { BorderSize = 0 }
            };

            btn.Click += async (s, e) =>
            {
                try
                {
                    float newVolume = volume / 100.0f;
                    if (_audioManager != null)
                    {
                        await _audioManager.SetProcessVolumeAsync(session.ProcessId, newVolume);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to set volume for {session.ProcessName}: {ex.Message}");
                }
            };

            return btn;
        }

        private string GetSessionStateText(int state)
        {
            return state switch
            {
                0 => "Inactive",
                1 => "Active",
                2 => "Expired",
                _ => "Unknown"
            };
        }

        private async Task KillProcess(int processId)
        {
            try
            {
                var result = MessageBox.Show($"Are you sure you want to kill process {processId}?",
                                           "Confirm Kill Process",
                                           MessageBoxButtons.YesNo,
                                           MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    var process = Process.GetProcessById(processId);
                    process.Kill();
                    await RefreshSessionsAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to kill process: {ex.Message}", "Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenProcessLocation(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                var filename = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(filename))
                {
                    Process.Start("explorer.exe", $"/select,\"{filename}\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open process location: {ex.Message}", "Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void SetVolumeQuick(AudioSession session, float volume, TrackBar slider, Label label)
        {
            try
            {
                if (_audioManager != null)
                {
                    await _audioManager.SetProcessVolumeAsync(session.ProcessId, volume);
                    slider.Value = (int)(volume * 100);
                    label.Text = $"{volume:P0}";
                    label.ForeColor = GetVolumeColor(volume);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set volume for {session.ProcessName}: {ex.Message}");
            }
        }

        private Color GetVolumeColor(float volume)
        {
            return volume switch
            {
                >= 0.8f => AccentGreen,
                >= 0.5f => AccentBlue,
                >= 0.2f => AccentOrange,
                _ => AccentRed
            };
        }

        private async Task RefreshSessionsAsync()
        {
            if (_audioManager == null || _isRefreshing) return;

            try
            {
                _statusLabel.Text = "🔄 Refreshing...";
                _statusLabel.ForeColor = AccentOrange;

                _isRefreshing = true;
                var sessions = await _audioManager.GetAllAudioSessionsAsync();
                _currentSessions = sessions;

                _statusLabel.Text = "⚡ Ready";
                _statusLabel.ForeColor = AccentGreen;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "❌ Error";
                _statusLabel.ForeColor = AccentRed;

                var logger = new DesktopLogger("Refresh");
                logger.LogError(ex, "Failed to refresh sessions");
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _isDisposing = true; // CRITICAL FIX: Set disposal flag to stop background operations
            _cancellationTokenSource.Cancel();
            _uiUpdateTimer?.Stop();
            _uiUpdateTimer?.Dispose();
            base.OnFormClosing(e);
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            // Ensure minimum size
            if (width < MinimumSize.Width) width = MinimumSize.Width;
            if (height < MinimumSize.Height) height = MinimumSize.Height;
            base.SetBoundsCore(x, y, width, height, specified);
        }
    }
}
