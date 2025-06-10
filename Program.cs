using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

using UniMixerServer.Communication;
using UniMixerServer.Configuration;
using UniMixerServer.Core;
using UniMixerServer.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace UniMixerServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Check if we should run the audio manager test
            if (args.Length > 0 && args[0].Equals("--test-audio", StringComparison.OrdinalIgnoreCase))
            {
                await AudioManagerTester.RunTest(args);
                return;
            }

            // Check if we should run the desktop app
            if (args.Length > 0 && args[0].Equals("--desktop", StringComparison.OrdinalIgnoreCase))
            {
                UniMixerServer.UI.DesktopAppLauncher.Launch();
                return;
            }

            // Load .env file for credentials
            EnvLoader.Load();

            // Create host builder
            var hostBuilder = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(Directory.GetCurrentDirectory())
                          .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                          .AddEnvironmentVariables()
                          .AddCommandLine(args);
                })
                .ConfigureServices((context, services) =>
                {
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

                    // Register core services
                    services.AddSingleton<IAudioManager>(provider =>
                    {
                        var logger = provider.GetRequiredService<ILogger<AudioManager>>();
                        return new AudioManager(logger, appConfig.Audio.EnableDetailedLogging);
                    });

                    // Register communication handlers conditionally
                    if (appConfig.EnableMqtt)
                    {
                        services.AddSingleton<ICommunicationHandler>(provider =>
                            new MqttHandler(
                                provider.GetRequiredService<ILogger<MqttHandler>>(),
                                appConfig.Mqtt));
                    }

                    if (appConfig.EnableSerial)
                    {
                        services.AddSingleton<ICommunicationHandler>(provider =>
                            new SerialHandler(
                                provider.GetRequiredService<ILogger<SerialHandler>>(),
                                appConfig.Serial));
                    }

                    // Register main service
                    services.AddHostedService<UniMixerService>();
                })
                .UseSerilog((context, services, configuration) =>
                {
                    var appConfig = context.Configuration.Get<AppConfig>() ?? new AppConfig();

                    configuration
                        .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("Application", "UniMixerServer")
                        .Enrich.WithProperty("MachineName", Environment.MachineName);

                    if (appConfig.Logging.EnableConsoleLogging)
                    {
                        configuration.WriteTo.Console(
                            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
                    }

                    if (appConfig.Logging.EnableFileLogging)
                    {
                        var logPath = appConfig.Logging.LogFilePath.Replace("-", $"-{DateTime.Now:yyyyMMdd}-");
                        configuration.WriteTo.File(
                            logPath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: appConfig.Logging.MaxLogFiles,
                            fileSizeLimitBytes: appConfig.Logging.MaxLogFileSizeMB * 1024 * 1024,
                            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
                    }
                })
                .UseWindowsService(); // Enable running as Windows Service

            try
            {
                Console.WriteLine("UniMixer Server starting...");
                Console.WriteLine($"Device ID: {Environment.MachineName}");
                Console.WriteLine("Communication: Serial only (MQTT disabled)");
                Console.WriteLine("Press Ctrl+C to stop the service");

                var host = hostBuilder.Build();

                // Print final configuration after all cascading initialization
                var finalConfig = host.Services.GetRequiredService<AppConfig>();
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

                if (finalConfig.EnableMqtt)
                {
                    Console.WriteLine("\n--- MQTT Configuration ---");
                    Console.WriteLine($"Broker Host: {finalConfig.Mqtt.BrokerHost}");
                    Console.WriteLine($"Broker Port: {finalConfig.Mqtt.BrokerPort}");
                    Console.WriteLine($"Client ID: {finalConfig.Mqtt.ClientId}");
                    Console.WriteLine($"Username: {(string.IsNullOrEmpty(finalConfig.Mqtt.Username) ? "(empty)" : "***set***")}");
                    Console.WriteLine($"Password: {(string.IsNullOrEmpty(finalConfig.Mqtt.Password) ? "(empty)" : "***set***")}");
                    Console.WriteLine($"Use TLS: {finalConfig.Mqtt.UseTls}");
                }

                Console.WriteLine("===============================\n");

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Environment.Exit(1);
            }
        }
    }
}

