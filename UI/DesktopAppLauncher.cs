using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using UniMixerServer.Core;

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
                using var form = new AudioSessionVisualizerForm();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start desktop application: {ex.Message}", 
                    "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public partial class AudioSessionVisualizerForm : Form
    {
        private readonly AudioManager _audioManager;
        private Timer _refreshTimer;
        private DataGridView _sessionGrid;
        private ComboBox _configComboBox;
        private Label _statusLabel;
        private Label _totalSessionsLabel;
        private Button _refreshButton;
        private Button _exportButton;
        private ProgressBar _refreshProgress;
        private CheckBox _autoRefreshCheckBox;
        private Panel _volumeDisplayPanel;
        private Panel _statsPanel;

        private List<AudioSession> _lastSessions = new List<AudioSession>();

        public AudioSessionVisualizerForm()
        {
            // Create AudioManager with null logger for desktop app
            _audioManager = new AudioManager(null, enableDetailedLogging: false);
            
            InitializeComponent();
            InitializeTimer();
            
            // Load initial data
            _ = RefreshDataAsync();
        }

        private void InitializeComponent()
        {
            this.Text = "🎵 Audio Session Visualizer";
            this.Size = new Size(1200, 800);
            this.MinimumSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = SystemIcons.Application;

            // Create main layout
            var mainPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10)
            };
            
            // Set up row styles
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F)); // Header
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F)); // Stats
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 200F)); // Volume Display
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Grid

            this.Controls.Add(mainPanel);

            CreateHeaderPanel(mainPanel);
            CreateStatsPanel(mainPanel);
            CreateVolumeDisplayPanel(mainPanel);
            CreateDataGridPanel(mainPanel);
        }

        private void CreateHeaderPanel(TableLayoutPanel mainPanel)
        {
            var headerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(240, 240, 240)
            };

            var titleLabel = new Label
            {
                Text = "🎵 Audio Session Monitor",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(51, 51, 51),
                AutoSize = true,
                Location = new Point(10, 15)
            };

            _totalSessionsLabel = new Label
            {
                Text = "Total Sessions: 0",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(102, 102, 102),
                AutoSize = true,
                Location = new Point(10, 40)
            };

            _configComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 200,
                Location = new Point(300, 20)
            };
            _configComboBox.Items.AddRange(new object[] {
                "Default Configuration",
                "All Devices",
                "With Capture Devices",
                "All + Capture"
            });
            _configComboBox.SelectedIndex = 1; // Default to "All Devices"
            _configComboBox.SelectedIndexChanged += ConfigComboBox_SelectedIndexChanged;

            _refreshButton = new Button
            {
                Text = "🔄 Refresh",
                Size = new Size(80, 30),
                Location = new Point(510, 18),
                BackColor = Color.FromArgb(0, 123, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _refreshButton.Click += RefreshButton_Click;

            _exportButton = new Button
            {
                Text = "📊 Export",
                Size = new Size(80, 30),
                Location = new Point(600, 18),
                BackColor = Color.FromArgb(40, 167, 69),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _exportButton.Click += ExportButton_Click;

            _autoRefreshCheckBox = new CheckBox
            {
                Text = "Auto Refresh (5s)",
                Location = new Point(690, 23),
                AutoSize = true,
                Checked = true
            };
            _autoRefreshCheckBox.CheckedChanged += AutoRefreshCheckBox_CheckedChanged;

            _refreshProgress = new ProgressBar
            {
                Size = new Size(200, 20),
                Location = new Point(800, 25),
                Style = ProgressBarStyle.Marquee,
                Visible = false
            };

            _statusLabel = new Label
            {
                Text = "Ready",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.Green,
                AutoSize = true,
                Location = new Point(1010, 28)
            };

            headerPanel.Controls.AddRange(new Control[] {
                titleLabel, _totalSessionsLabel, _configComboBox, _refreshButton, 
                _exportButton, _autoRefreshCheckBox, _refreshProgress, _statusLabel
            });

            mainPanel.Controls.Add(headerPanel, 0, 0);
        }

        private void CreateStatsPanel(TableLayoutPanel mainPanel)
        {
            _statsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var statsTitle = new Label
            {
                Text = "📊 Session Statistics",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(51, 51, 51),
                Location = new Point(10, 10),
                AutoSize = true
            };

            _statsPanel.Controls.Add(statsTitle);
            mainPanel.Controls.Add(_statsPanel, 0, 1);
        }

        private void CreateVolumeDisplayPanel(TableLayoutPanel mainPanel)
        {
            _volumeDisplayPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true
            };

            var volumeTitle = new Label
            {
                Text = "🔊 Volume Levels",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(51, 51, 51),
                Location = new Point(10, 10),
                AutoSize = true
            };

            _volumeDisplayPanel.Controls.Add(volumeTitle);
            mainPanel.Controls.Add(_volumeDisplayPanel, 0, 2);
        }

        private void CreateDataGridPanel(TableLayoutPanel mainPanel)
        {
            _sessionGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };

            // Define columns
            _sessionGrid.Columns.AddRange(new DataGridViewColumn[]
            {
                new DataGridViewTextBoxColumn 
                { 
                    Name = "ProcessName", 
                    HeaderText = "Process", 
                    Width = 150,
                    DataPropertyName = "ProcessName"
                },
                new DataGridViewTextBoxColumn 
                { 
                    Name = "ProcessId", 
                    HeaderText = "PID", 
                    Width = 80,
                    DataPropertyName = "ProcessId"
                },
                new DataGridViewTextBoxColumn 
                { 
                    Name = "DisplayName", 
                    HeaderText = "Display Name", 
                    Width = 200,
                    DataPropertyName = "DisplayName"
                },
                new DataGridViewTextBoxColumn 
                { 
                    Name = "Volume", 
                    HeaderText = "Volume", 
                    Width = 80,
                    DataPropertyName = "VolumePercent"
                },
                new DataGridViewTextBoxColumn 
                { 
                    Name = "IsMuted", 
                    HeaderText = "Muted", 
                    Width = 80,
                    DataPropertyName = "MuteStatus"
                },
                new DataGridViewTextBoxColumn 
                { 
                    Name = "SessionState", 
                    HeaderText = "State", 
                    Width = 100,
                    DataPropertyName = "StateText"
                },
                new DataGridViewTextBoxColumn 
                { 
                    Name = "LastUpdated", 
                    HeaderText = "Last Updated", 
                    Width = 120,
                    DataPropertyName = "LastUpdatedText"
                }
            });

            // Style the grid
            _sessionGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 123, 255);
            _sessionGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            _sessionGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 250);
            _sessionGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(51, 51, 51);
            _sessionGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);

            mainPanel.Controls.Add(_sessionGrid, 0, 3);
        }

        private void InitializeTimer()
        {
            _refreshTimer = new Timer();
            _refreshTimer.Interval = 5000; // 5 seconds
            _refreshTimer.Tick += async (s, e) => await RefreshDataAsync();
            _refreshTimer.Start();
        }

        private AudioDiscoveryConfig GetCurrentConfig()
        {
            return _configComboBox.SelectedIndex switch
            {
                0 => new AudioDiscoveryConfig(), // Default
                1 => new AudioDiscoveryConfig { IncludeAllDevices = true }, // All Devices
                2 => new AudioDiscoveryConfig { IncludeCaptureDevices = true }, // With Capture
                3 => new AudioDiscoveryConfig { IncludeAllDevices = true, IncludeCaptureDevices = true }, // All + Capture
                _ => new AudioDiscoveryConfig()
            };
        }

        private async Task RefreshDataAsync()
        {
            try
            {
                _statusLabel.Text = "Refreshing...";
                _statusLabel.ForeColor = Color.Orange;
                _refreshProgress.Visible = true;
                _refreshButton.Enabled = false;

                var config = GetCurrentConfig();
                var sessions = await _audioManager.GetAllAudioSessionsAsync(config);
                
                UpdateUI(sessions);
                _lastSessions = sessions;

                _statusLabel.Text = $"Updated: {DateTime.Now:HH:mm:ss}";
                _statusLabel.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Error: {ex.Message}";
                _statusLabel.ForeColor = Color.Red;
            }
            finally
            {
                _refreshProgress.Visible = false;
                _refreshButton.Enabled = true;
            }
        }

        private void UpdateUI(List<AudioSession> sessions)
        {
            UpdateStatsPanel(sessions);
            UpdateVolumeDisplay(sessions);
            UpdateDataGrid(sessions);
            UpdateStatusLabels(sessions);
        }

        private void UpdateStatsPanel(List<AudioSession> sessions)
        {
            // Clear existing stats
            for (int i = _statsPanel.Controls.Count - 1; i >= 1; i--)
            {
                _statsPanel.Controls.RemoveAt(i);
            }

            var stats = new[]
            {
                $"Total Sessions: {sessions.Count}",
                $"Active: {sessions.Count(s => s.SessionState == 1)}",
                $"Inactive: {sessions.Count(s => s.SessionState == 0)}",
                $"Muted: {sessions.Count(s => s.IsMuted)}",
                $"Avg Volume: {(sessions.Any() ? sessions.Average(s => s.Volume) * 100 : 0):F1}%"
            };

            for (int i = 0; i < stats.Length; i++)
            {
                var statLabel = new Label
                {
                    Text = stats[i],
                    Font = new Font("Segoe UI", 10F),
                    ForeColor = Color.FromArgb(51, 51, 51),
                    Location = new Point(20 + (i * 180), 40),
                    AutoSize = true
                };
                _statsPanel.Controls.Add(statLabel);
            }
        }

        private void UpdateVolumeDisplay(List<AudioSession> sessions)
        {
            // Clear existing volume bars
            for (int i = _volumeDisplayPanel.Controls.Count - 1; i >= 1; i--)
            {
                _volumeDisplayPanel.Controls.RemoveAt(i);
            }

            var processGroups = sessions
                .GroupBy(s => s.ProcessName)
                .Select(g => new {
                    Name = g.Key,
                    Volume = g.Average(s => s.Volume) * 100,
                    IsMuted = g.Any(s => s.IsMuted),
                    Count = g.Count()
                })
                .OrderByDescending(p => p.Volume)
                .Take(8)
                .ToList();

            int y = 40;
            foreach (var process in processGroups)
            {
                // Process name
                var nameLabel = new Label
                {
                    Text = $"{process.Name} ({process.Count})",
                    Font = new Font("Segoe UI", 9F),
                    Location = new Point(10, y),
                    Size = new Size(150, 20)
                };

                // Volume bar background
                var bgBar = new Panel
                {
                    Location = new Point(170, y + 2),
                    Size = new Size(200, 16),
                    BackColor = Color.LightGray,
                    BorderStyle = BorderStyle.FixedSingle
                };

                // Volume bar fill
                var fillBar = new Panel
                {
                    Location = new Point(0, 0),
                    Size = new Size((int)(200 * process.Volume / 100), 16),
                    BackColor = process.IsMuted ? Color.Red : Color.FromArgb(54, 162, 235)
                };
                bgBar.Controls.Add(fillBar);

                // Volume percentage
                var volumeLabel = new Label
                {
                    Text = $"{process.Volume:F1}%{(process.IsMuted ? " (Muted)" : "")}",
                    Font = new Font("Segoe UI", 8F),
                    Location = new Point(380, y),
                    Size = new Size(100, 20)
                };

                _volumeDisplayPanel.Controls.AddRange(new Control[] { nameLabel, bgBar, volumeLabel });
                y += 25;
            }
        }

        private void UpdateDataGrid(List<AudioSession> sessions)
        {
            var gridData = sessions.Select(s => new
            {
                ProcessName = s.ProcessName,
                ProcessId = s.ProcessId,
                DisplayName = string.IsNullOrEmpty(s.DisplayName) ? "[No Display Name]" : s.DisplayName,
                VolumePercent = $"{s.Volume:P0}",
                MuteStatus = s.IsMuted ? "🔇 Muted" : "🔊 Unmuted",
                StateText = GetStateText(s.SessionState),
                LastUpdatedText = s.LastUpdated.ToString("HH:mm:ss")
            }).ToList();

            _sessionGrid.DataSource = gridData;
        }

        private void UpdateStatusLabels(List<AudioSession> sessions)
        {
            _totalSessionsLabel.Text = $"Total Sessions: {sessions.Count}";
        }

        private string GetStateText(int state)
        {
            return state switch
            {
                0 => "Inactive",
                1 => "Active",
                2 => "Expired",
                _ => "Unknown"
            };
        }

        private async void RefreshButton_Click(object sender, EventArgs e)
        {
            await RefreshDataAsync();
        }

        private async void ConfigComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            await RefreshDataAsync();
        }

        private void AutoRefreshCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Enabled = _autoRefreshCheckBox.Checked;
            }
        }

        private void ExportButton_Click(object sender, EventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|HTML files (*.html)|*.html",
                    DefaultExt = "csv",
                    FileName = $"audio_sessions_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    if (saveDialog.FilterIndex == 1) // CSV
                    {
                        ExportToCsv(saveDialog.FileName);
                        MessageBox.Show($"CSV data exported to:\n{saveDialog.FileName}", "Export Successful", 
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else // HTML
                    {
                        AudioSessionVisualizer.ExportToHtml(_lastSessions, _lastSessions, _lastSessions, saveDialog.FileName);
                        MessageBox.Show($"HTML report exported to:\n{saveDialog.FileName}", "Export Successful", 
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportToCsv(string filename)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("ProcessName,ProcessId,DisplayName,Volume,IsMuted,SessionState,LastUpdated");
            
            foreach (var session in _lastSessions)
            {
                csv.AppendLine($"\"{session.ProcessName}\",{session.ProcessId},\"{session.DisplayName}\"," +
                             $"{session.Volume:F2},{session.IsMuted},{session.SessionState},{session.LastUpdated:yyyy-MM-dd HH:mm:ss}");
            }

            System.IO.File.WriteAllText(filename, csv.ToString());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer?.Stop();
                _refreshTimer?.Dispose();
                _audioManager?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
} 