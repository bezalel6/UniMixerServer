using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace UniMixerServer.Services{
    public class EspExceptionDecoder{
        private readonly ILogger<EspExceptionDecoder> _logger;
        private readonly string _debugFilesPath;
        private readonly string _toolchainPath;
        private readonly string _decoderScriptPath;
        private readonly StringBuilder _crashBuffer = new StringBuilder();
        private bool _isCapturingCrash = false;

        // ESP32 exception patterns
        private static readonly Regex GuruMeditationRegex = new Regex(
            @"Guru Meditation Error: Core\s+(\d+)\s+panic'ed\s+\((.+?)\)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        private static readonly Regex BacktraceRegex = new Regex(
            @"Backtrace:\s+((?:0x[0-9a-fA-F]+:0x[0-9a-fA-F]+\s*)+)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        private static readonly Regex RegisterDumpRegex = new Regex(
            @"Core\s+\d+\s+register\s+dump:", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public EspExceptionDecoder(ILogger<EspExceptionDecoder> logger){
            _logger = logger;
            _debugFilesPath = Path.Combine(Directory.GetCurrentDirectory(), "debug_files");
            _toolchainPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                ".platformio", "packages", "toolchain-xtensa-esp32", "bin", "xtensa-esp32-elf-addr2line.exe");
            _decoderScriptPath = Path.Combine(Directory.GetCurrentDirectory(), "tools", "esp-exception-decoder", "decoder.py");
        }

        /// <summary>
        /// Processes incoming serial data to detect and decode ESP32 crashes
        /// </summary>
        /// <param name="data">Raw serial data string</param>
        /// <returns>True if a crash was detected and decoded, false otherwise</returns>
        public bool ProcessSerialData(string data){
            if (string.IsNullOrEmpty(data)) return false;

            var lines = data.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines){
                var trimmedLine = line.Trim();
                
                // Check for start of crash dump
                if (GuruMeditationRegex.IsMatch(trimmedLine)){
                    _logger.LogWarning("ESP32 CRASH DETECTED: {Line}", trimmedLine);
                    _isCapturingCrash = true;
                    _crashBuffer.Clear();
                    _crashBuffer.AppendLine(trimmedLine);
                    continue;
                }
                
                // If we're capturing a crash, add all lines
                if (_isCapturingCrash){
                    _crashBuffer.AppendLine(trimmedLine);
                    
                    // Check if this is the end of the crash dump
                    if (trimmedLine.StartsWith("ELF file SHA256:") || 
                        trimmedLine.Contains("Rebooting...") ||
                        trimmedLine.Contains("rst:") ||
                        (BacktraceRegex.IsMatch(trimmedLine) && _crashBuffer.Length > 500)){
                        
                        // We have a complete crash dump
                        var crashData = _crashBuffer.ToString();
                        _logger.LogError("Complete ESP32 crash dump captured:\n{CrashData}", crashData);
                        
                        // Decode the crash
                        var decoded = DecodeException(crashData);
                        if (decoded){
                            _logger.LogCritical("ESP32 CRASH DECODED - Application will exit for safety");
                            
                            // Exit the application after crash decoding
                            Environment.Exit(1);
                        }
                        
                        _isCapturingCrash = false;
                        return true;
                    }
                }
            }
            
            return false;
        }

        /// <summary>
        /// Decodes an ESP32 exception using the Python decoder script
        /// </summary>
        /// <param name="crashData">Raw crash dump data</param>
        /// <returns>True if decoding was successful</returns>
        private bool DecodeException(string crashData){
            try{
                // Check if required files exist
                if (!File.Exists(_decoderScriptPath)){
                    _logger.LogError("Exception decoder script not found at: {Path}", _decoderScriptPath);
                    return false;
                }

                var elfPath = Path.Combine(_debugFilesPath, "firmware.elf");
                if (!File.Exists(elfPath)){
                    _logger.LogError("Firmware ELF file not found at: {Path}", elfPath);
                    _logger.LogError("Make sure your build system uploads the firmware.elf file");
                    return false;
                }

                if (!File.Exists(_toolchainPath)){
                    _logger.LogError("ESP32 toolchain not found at: {Path}", _toolchainPath);
                    return false;
                }

                // Create temporary crash file
                var tempCrashFile = Path.Combine(Path.GetTempPath(), $"esp32_crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(tempCrashFile, crashData);

                _logger.LogInformation("Decoding ESP32 crash using:");
                _logger.LogInformation("  - ELF file: {ElfPath}", elfPath);
                _logger.LogInformation("  - Toolchain: {ToolchainPath}", _toolchainPath);
                _logger.LogInformation("  - Crash file: {CrashFile}", tempCrashFile);

                // Run the decoder
                var processInfo = new ProcessStartInfo{
                    FileName = "python",
                    Arguments = $"\"{_decoderScriptPath}\" -p ESP32 -t \"{_toolchainPath}\" -e \"{elfPath}\" \"{tempCrashFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process == null){
                    _logger.LogError("Failed to start decoder process");
                    return false;
                }

                process.WaitForExit(30000); // 30 second timeout

                var output = process.StandardOutput.ReadToEnd();
                var errorOutput = process.StandardError.ReadToEnd();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output)){
                    _logger.LogCritical("ESP32 CRASH DECODED SUCCESSFULLY:");
                    _logger.LogCritical("=====================================");
                    _logger.LogCritical("{DecodedOutput}", output);
                    _logger.LogCritical("=====================================");
                    
                    // Save decoded output to file
                    var decodedFile = Path.Combine(_debugFilesPath, $"decoded_crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.WriteAllText(decodedFile, $"Original Crash:\n{crashData}\n\nDecoded Output:\n{output}");
                    _logger.LogInformation("Decoded crash saved to: {DecodedFile}", decodedFile);
                    
                    return true;
                }
                else{
                    _logger.LogError("Decoder process failed with exit code {ExitCode}", process.ExitCode);
                    if (!string.IsNullOrEmpty(errorOutput)){
                        _logger.LogError("Decoder error output: {ErrorOutput}", errorOutput);
                    }
                    return false;
                }
            }
            catch (Exception ex){
                _logger.LogError(ex, "Exception occurred while decoding ESP32 crash");
                return false;
            }
        }

        /// <summary>
        /// Manually decode a crash file (for testing purposes)
        /// </summary>
        /// <param name="crashFilePath">Path to the crash file</param>
        /// <returns>True if decoding was successful</returns>
        public bool DecodeCrashFile(string crashFilePath){
            if (!File.Exists(crashFilePath)){
                _logger.LogError("Crash file not found: {Path}", crashFilePath);
                return false;
            }

            var crashData = File.ReadAllText(crashFilePath);
            return DecodeException(crashData);
        }
    }
} 
