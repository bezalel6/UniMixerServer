using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

using UniMixerServer.Communication;
using UniMixerServer.Communication.MessageProcessing;
using UniMixerServer.Configuration;
using UniMixerServer.Core;
using UniMixerServer.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace UniMixerServer {
    class Program {
        static async Task Main(string[] args) {
            // Check if we should test Chrome icon conversion
            if (args.Length > 0 && args[0].Equals("--test-chrome", StringComparison.OrdinalIgnoreCase)) {
                await TestChromeIconConversion();
                return;
            }

            // Check if we should run the audio manager test
            if (args.Length > 0 && args[0].Equals("--test-audio", StringComparison.OrdinalIgnoreCase)) {
                await AudioManagerTester.RunTest(args);
                return;
            }

            // Check if we should run the desktop app
            if (args.Length > 0 && args[0].Equals("--desktop", StringComparison.OrdinalIgnoreCase)) {
                UniMixerServer.UI.DesktopAppLauncher.Launch();
                return;
            }

            // Load .env file for credentials
            EnvLoader.Load();

            // Create host builder
            var hostBuilder = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) => {
                    config.SetBasePath(Directory.GetCurrentDirectory())
                          .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                          .AddEnvironmentVariables()
                          .AddCommandLine(args);
                })
                .ConfigureServices((context, services) => {
                    // Get configuration
                    var configuration = context.Configuration;
                    var appConfig = configuration.Get<AppConfig>() ?? new AppConfig();

                    // Set default values if not provided
                    if (string.IsNullOrEmpty(appConfig.DeviceId))
                        appConfig.DeviceId = Environment.GetEnvironmentVariable("DEVICE_ID") ?? Environment.MachineName;

                    if (string.IsNullOrEmpty(appConfig.Mqtt.ClientId))
                        appConfig.Mqtt.ClientId = $"unimixer-{Environment.MachineName}";

                    // Override with environment variables for credentials
                    var mqttBrokerUrl = Environment.GetEnvironmentVariable("MQTT_BROKER_URL");
                    var mqttClientId = Environment.GetEnvironmentVariable("MQTT_CLIENT_ID");
                    var mqttUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME");
                    var mqttPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD");

                    if (!string.IsNullOrEmpty(mqttBrokerUrl))
                        appConfig.Mqtt.BrokerHost = mqttBrokerUrl;

                    if (!string.IsNullOrEmpty(mqttClientId))
                        appConfig.Mqtt.ClientId = mqttClientId;

                    if (!string.IsNullOrEmpty(mqttUsername))
                        appConfig.Mqtt.Username = mqttUsername;

                    if (!string.IsNullOrEmpty(mqttPassword))
                        appConfig.Mqtt.Password = mqttPassword;

                    // Register configuration
                    services.Configure<AppConfig>(configuration);
                    services.AddSingleton(appConfig);

                    // Register process icon extractor
                    services.AddSingleton<IProcessIconExtractor, ProcessIconExtractor>();

                    // Register core services
                    services.AddSingleton<IAudioManager>(provider => {
                        var logger = provider.GetRequiredService<ILogger<AudioManager>>();
                        var iconExtractor = provider.GetRequiredService<IProcessIconExtractor>();
                        return new AudioManager(logger, appConfig.Audio.EnableDetailedLogging, iconExtractor);
                    });

                    // Register status update processor
                    services.AddSingleton<StatusUpdateProcessor>();

                    // Register asset service
                    services.AddSingleton<IAssetService>(provider => {
                        var logger = provider.GetRequiredService<ILogger<AssetService>>();
                        var iconExtractor = provider.GetRequiredService<IProcessIconExtractor>();
                        return new AssetService(logger, iconExtractor);
                    });

                    // Register message processor for O(1) lookup
                    services.AddSingleton<JsonMessageProcessor>();

                    // Register communication handlers conditionally
                    if (appConfig.EnableMqtt) {
                        services.AddSingleton<ICommunicationHandler>(provider =>
                            new MqttHandler(
                                provider.GetRequiredService<ILogger<MqttHandler>>(),
                                appConfig.Mqtt,
                                provider.GetRequiredService<JsonMessageProcessor>()));
                    }

                    if (appConfig.EnableSerial) {
                        services.AddSingleton<ICommunicationHandler>(provider =>
                            new SerialHandler(
                                provider.GetRequiredService<ILogger<SerialHandler>>(),
                                appConfig.Serial,
                                provider.GetRequiredService<JsonMessageProcessor>()));
                    }

                    // Register main service
                    services.AddHostedService<UniMixerService>();
                })
                .UseSerilog((context, services, configuration) => {
                    var appConfig = context.Configuration.Get<AppConfig>() ?? new AppConfig();

                    // Convert string log level to Serilog LogEventLevel
                    var logLevel = appConfig.Logging.LogLevel.ToUpperInvariant() switch {
                        "DEBUG" => Serilog.Events.LogEventLevel.Debug,
                        "INFORMATION" or "INFO" => Serilog.Events.LogEventLevel.Information,
                        "WARNING" or "WARN" => Serilog.Events.LogEventLevel.Warning,
                        "ERROR" => Serilog.Events.LogEventLevel.Error,
                        "FATAL" => Serilog.Events.LogEventLevel.Fatal,
                        _ => Serilog.Events.LogEventLevel.Information
                    };

                    configuration
                        .MinimumLevel.Is(logLevel)
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("Application", "UniMixerServer")
                        .Enrich.WithProperty("MachineName", Environment.MachineName);

                    if (appConfig.Logging.EnableConsoleLogging) {
                        configuration.WriteTo.Console(
                            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
                    }

                    if (appConfig.Logging.EnableFileLogging) {
                        var logPath = appConfig.Logging.LogFilePath;
                        configuration.WriteTo.File(
                            logPath,
                            fileSizeLimitBytes: appConfig.Logging.MaxLogFileSizeMB * 1024 * 1024,
                            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
                    }
                })
                .UseWindowsService(); // Enable running as Windows Service

            try {
                Console.WriteLine("UniMixer Server starting...");
                Console.WriteLine($"Device ID: {Environment.MachineName}");
                Console.WriteLine("Communication: Serial only (MQTT disabled)");
                Console.WriteLine("Press Ctrl+C to stop the service");

                var host = hostBuilder.Build();

                // Print final configuration after all cascading initialization
                var finalConfig = host.Services.GetRequiredService<AppConfig>();

                // Initialize configurable data loggers
                IncomingDataLogger.Initialize(finalConfig.Logging);
                OutgoingDataLogger.Initialize(finalConfig.Logging);
                Console.WriteLine("\n=== FINAL CONFIGURATION ===");
                Console.WriteLine($"Device ID: {finalConfig.DeviceId}");
                Console.WriteLine($"Status Broadcast Interval: {finalConfig.StatusBroadcastIntervalMs}ms");
                Console.WriteLine($"Audio Session Refresh Interval: {finalConfig.AudioSessionRefreshIntervalMs}ms");
                Console.WriteLine($"Enable MQTT: {finalConfig.EnableMqtt}");
                Console.WriteLine($"Enable Serial: {finalConfig.EnableSerial}");

                Console.WriteLine("\n--- Logging Configuration ---");
                Console.WriteLine($"Log Level: {finalConfig.Logging.LogLevel}");
                Console.WriteLine($"Enable Console Logging: {finalConfig.Logging.EnableConsoleLogging}");
                Console.WriteLine($"Enable File Logging: {finalConfig.Logging.EnableFileLogging}");
                Console.WriteLine($"Log File Path: {finalConfig.Logging.LogFilePath}");
                Console.WriteLine($"Max Log File Size: {finalConfig.Logging.MaxLogFileSizeMB}MB");
                Console.WriteLine($"Max Log Files: {finalConfig.Logging.MaxLogFiles}");
                Console.WriteLine($"Enable Incoming Data Logging: {finalConfig.Logging.EnableIncomingDataLogging}");
                Console.WriteLine($"Enable Outgoing Data Logging: {finalConfig.Logging.EnableOutgoingDataLogging}");
                Console.WriteLine($"Incoming Data Log Path: {finalConfig.Logging.IncomingDataLogPath}");
                Console.WriteLine($"Outgoing Data Log Path: {finalConfig.Logging.OutgoingDataLogPath}");
                Console.WriteLine($"Max Data Log File Size: {finalConfig.Logging.MaxDataLogFileSizeMB}MB");
                Console.WriteLine($"Max Data Log Files: {finalConfig.Logging.MaxDataLogFiles}");

                Console.WriteLine("\n--- Audio Configuration ---");
                Console.WriteLine($"Include All Devices: {finalConfig.Audio.IncludeAllDevices}");
                Console.WriteLine($"Include Capture Devices: {finalConfig.Audio.IncludeCaptureDevices}");
                Console.WriteLine($"Data Flow: {finalConfig.Audio.DataFlow}");
                Console.WriteLine($"Device Role: {finalConfig.Audio.DeviceRole}");
                Console.WriteLine($"Enable Detailed Logging: {finalConfig.Audio.EnableDetailedLogging}");

                Console.WriteLine("\n--- Serial Configuration ---");
                Console.WriteLine($"Port Name: {finalConfig.Serial.PortName}");
                Console.WriteLine($"Baud Rate: {finalConfig.Serial.BaudRate}");
                Console.WriteLine($"Enable Auto Reconnect: {finalConfig.Serial.EnableAutoReconnect}");

                if (finalConfig.EnableMqtt) {
                    Console.WriteLine("\n--- MQTT Configuration ---");
                    Console.WriteLine($"Broker Host: {finalConfig.Mqtt.BrokerHost}");
                    Console.WriteLine($"Broker Port: {finalConfig.Mqtt.BrokerPort}");
                    Console.WriteLine($"Client ID: {finalConfig.Mqtt.ClientId}");
                    Console.WriteLine($"Username: {(string.IsNullOrEmpty(finalConfig.Mqtt.Username) ? "(empty)" : "***set***")}");
                    Console.WriteLine($"Password: {(string.IsNullOrEmpty(finalConfig.Mqtt.Password) ? "(empty)" : "***set***")}");
                    Console.WriteLine($"Use TLS: {finalConfig.Mqtt.UseTls}");
                }

                Console.WriteLine("===============================\n");

                // Setup cleanup for data loggers
                AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                    Console.WriteLine("Cleaning up data loggers...");
                    IncomingDataLogger.Dispose();
                    OutgoingDataLogger.Dispose();
                };

                await host.RunAsync();
            }
            catch (Exception ex) {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Environment.Exit(1);
            }
        }

        private static async Task TestChromeIconConversion() {
            Console.WriteLine("=== Chrome Icon Conversion Test ===");
            Console.WriteLine($"Starting test at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();

            try {
                // Setup basic console logging for the test
                var loggerFactory = LoggerFactory.Create(builder =>
                    builder.AddConsole()
                           .SetMinimumLevel(LogLevel.Information));

                // Create services
                Console.WriteLine("Creating services...");
                var iconExtractorLogger = loggerFactory.CreateLogger<ProcessIconExtractor>();
                var iconExtractor = new ProcessIconExtractor(iconExtractorLogger);

                var assetServiceLogger = loggerFactory.CreateLogger<AssetService>();
                var assetService = new AssetService(assetServiceLogger, iconExtractor);

                Console.WriteLine("✓ Services created successfully");
                Console.WriteLine();

                // Test Chrome icon conversion
                Console.WriteLine("Testing Chrome icon conversion...");
                Console.WriteLine("Looking for Chrome process names: chrome, chrome.exe, Google Chrome");
                Console.WriteLine();

                var chromeProcessNames = new[] { "chrome", "chrome.exe", "Google Chrome" };
                bool conversionSucceeded = false;
                string? successfulProcessName = null;

                foreach (var processName in chromeProcessNames) {
                    Console.WriteLine($"--- Testing process name: '{processName}' ---");

                    try {
                        var result = await assetService.GetAssetAsync(processName);

                        if (result.Success && result.AssetData != null && result.AssetData.Length > 0) {
                            Console.WriteLine($"✓ SUCCESS: Converted {processName} icon!");
                            Console.WriteLine($"  Format: LVGL Binary");
                            Console.WriteLine($"  Size: {result.AssetData.Length} bytes");
                            Console.WriteLine($"  Process: {result.ProcessName}");

                            if (result.Metadata != null) {
                                Console.WriteLine($"  Dimensions: {result.Metadata.Width}x{result.Metadata.Height}");
                                Console.WriteLine($"  File format: {result.Metadata.Format}");
                                Console.WriteLine($"  Checksum: {result.Metadata.Checksum}");
                            }

                            conversionSucceeded = true;
                            successfulProcessName = processName;
                            break;
                        }
                        else {
                            Console.WriteLine($"✗ FAILED: {result.ErrorMessage ?? "Unknown error"}");
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"✗ ERROR: {ex.Message}");
                    }

                    Console.WriteLine();
                }

                Console.WriteLine("=== Test Summary ===");
                if (conversionSucceeded) {
                    Console.WriteLine($"✓ Chrome icon conversion SUCCESSFUL using process name: '{successfulProcessName}'");
                    Console.WriteLine("Check the logs above for detailed conversion information.");
                    Console.WriteLine("Debug files have been preserved in the assets/debug/lvgl_conversion directory.");
                }
                else {
                    Console.WriteLine("✗ Chrome icon conversion FAILED for all attempted process names");
                    Console.WriteLine();
                    Console.WriteLine("Possible reasons:");
                    Console.WriteLine("- Chrome is not currently running");
                    Console.WriteLine("- lv_img_conv tool is not properly installed");
                    Console.WriteLine("- ts-node is not available in PATH");
                    Console.WriteLine("- Permission issues accessing Chrome process");
                    Console.WriteLine();
                    Console.WriteLine("To troubleshoot:");
                    Console.WriteLine("1. Make sure Chrome is running");
                    Console.WriteLine("2. Check the detailed logs above");
                    Console.WriteLine("3. Verify lv_img_conv installation in tools/lv_img_conv/lib/");
                    Console.WriteLine("4. Ensure ts-node is installed: npm install -g ts-node");
                }

                Console.WriteLine();
                Console.WriteLine($"Test completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex) {
                Console.WriteLine($"CRITICAL ERROR during test setup: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Environment.Exit(1);
            }
        }
    }
}

