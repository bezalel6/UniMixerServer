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
        private readonly string _addr2linePath;
        private readonly StringBuilder _crashBuffer = new StringBuilder();
        private readonly StringBuilder _lineBuffer = new StringBuilder(); // Buffer for building complete lines
        private bool _isCapturingCrash = false;
        private bool _crashDetected = false;
        private DateTime _crashStartTime = DateTime.MinValue;
        private const int MAX_CRASH_CAPTURE_SECONDS = 10; // Increased timeout for complete crash capture

        // ESP32-S3 (RISC-V) exception patterns
        private static readonly Regex GuruMeditationRegex = new Regex(
            @"Guru\s+Meditation\s+Error", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        private static readonly Regex BacktraceRegex = new Regex(
            @"Backtrace:\s*(0x[0-9a-fA-F]+:0x[0-9a-fA-F]+\s*)+", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ESP32-S3 RISC-V crash patterns
        private static readonly Regex PanicRegex = new Regex(
            @"panic'ed|LoadProhibited|StoreProhibited|InstrFetchProhibited|IllegalInstruction|abort\(\)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public EspExceptionDecoder(ILogger<EspExceptionDecoder> logger){
            _logger = logger;
            _debugFilesPath = Path.Combine(Directory.GetCurrentDirectory(), "debug_files");
            
            // Try to find ESP-IDF toolchain for ESP32-S3 (RISC-V)
            var possibleToolchainPaths = new[]
            {
                // Standard ESP-IDF installation (Windows)
                @"C:\Espressif\tools\riscv32-esp-elf",
                
                // ESP-IDF v5.x default paths
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    ".espressif", "tools", "riscv32-esp-elf"),
                    
                // PlatformIO ESP32-S3 toolchain path  
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    ".platformio", "packages", "toolchain-riscv32-esp"),
                    
                // System ESP-IDF installation
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 
                    "Espressif", "tools", "riscv32-esp-elf"),
                    
                // Manual ESP-IDF installation
                Path.Combine(@"C:\esp\esp-idf", "tools", "riscv32-esp-elf")
            };

            foreach (var basePath in possibleToolchainPaths){
                if (Directory.Exists(basePath)){
                    var toolchainDirs = Directory.GetDirectories(basePath, "*", SearchOption.TopDirectoryOnly);
                    foreach (var toolchainDir in toolchainDirs){
                        var addr2linePath = Path.Combine(toolchainDir, "riscv32-esp-elf", "bin", "riscv32-esp-elf-addr2line.exe");
                        if (File.Exists(addr2linePath)){
                            _toolchainPath = toolchainDir;
                            _addr2linePath = addr2linePath;
                            _logger.LogInformation("Found ESP32-S3 toolchain at: {Path}", _toolchainPath);
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(_toolchainPath)) break;
                }
            }

            if (string.IsNullOrEmpty(_toolchainPath)){
                _logger.LogWarning("ESP32-S3 toolchain not found. Exception decoding will be limited.");
            }
        }

        /// <summary>
        /// Check if crash detection is active - this should halt all normal processing
        /// </summary>
        public bool IsCrashDetectionActive => _isCapturingCrash || _crashDetected;

        /// <summary>
        /// Processes incoming serial data line by line to detect ESP32 crashes
        /// This is the main entry point - all other methods are simplified or removed
        /// </summary>
        /// <param name="data">Raw serial data string</param>
        /// <returns>True if a crash was detected and processed</returns>
        public bool ProcessSerialData(string data){
            if (string.IsNullOrEmpty(data)) return false;
            
            // Check for crash timeout
            if (_isCapturingCrash && DateTime.UtcNow - _crashStartTime > TimeSpan.FromSeconds(MAX_CRASH_CAPTURE_SECONDS)){
                _logger.LogWarning("Crash capture timeout reached, completing capture");
                return CompleteCrashCapture();
            }

            // Add incoming data to line buffer
            _lineBuffer.Append(data);
            
            // Process complete lines
            var bufferContent = _lineBuffer.ToString();
            var lines = bufferContent.Split(new[] { '\n', '\r' }, StringSplitOptions.None);
            
            // Keep the last incomplete line in buffer
            _lineBuffer.Clear();
            if (lines.Length > 0 && !bufferContent.EndsWith("\n") && !bufferContent.EndsWith("\r")){
                _lineBuffer.Append(lines[lines.Length - 1]);
                lines = lines.Take(lines.Length - 1).ToArray();
            }
            
            // Process each complete line
            foreach (var line in lines){
                if (ProcessLine(line.Trim())){
                    return true; // Crash detected and processed
                }
            }
            
            return false;
        }

        /// <summary>
        /// Process a single line for crash detection
        /// </summary>
        /// <param name="line">Complete line to process</param>
        /// <returns>True if crash was detected and processed</returns>
        private bool ProcessLine(string line){
            if (string.IsNullOrWhiteSpace(line)) return false;
            
            // If we're already capturing a crash, add this line to the buffer
            if (_isCapturingCrash){
                _crashBuffer.AppendLine(line);
                _logger.LogDebug("Crash capture: {Line}", line);
                
                // Check if this is the end of the crash dump
                if (IsEndOfCrashDump(line)){
                    return CompleteCrashCapture();
                }
                return false; // Continue capturing
            }
            
            // Only start new crash detection if we haven't already detected one
            if (!_crashDetected){
                // Check for Guru Meditation Error
                if (GuruMeditationRegex.IsMatch(line)){
                    _logger.LogCritical("🚨 ESP32 GURU MEDITATION ERROR DETECTED: {Line}", line);
                    StartCrashCapture(line);
                    return false; // Continue capturing more data
                }
                
                // Check for panic without full Guru Meditation
                if (PanicRegex.IsMatch(line) && !IsRegularDebugMessage(line)){
                    _logger.LogCritical("🚨 ESP32 PANIC DETECTED: {Line}", line);
                    StartCrashCapture(line);
                    return false; // Continue capturing more data
                }
            }
            
            return false;
        }

        /// <summary>
        /// Starts capturing a crash dump
        /// </summary>
        /// <param name="initialLine">The first line of the crash</param>
        private void StartCrashCapture(string initialLine){
            _logger.LogCritical("🚨 STARTING ESP32 CRASH CAPTURE - HALTING NORMAL PROCESSING");
            _isCapturingCrash = true;
            _crashStartTime = DateTime.UtcNow;
            _crashBuffer.Clear();
            _crashBuffer.AppendLine(initialLine);
        }

        /// <summary>
        /// Checks if the line indicates the end of a crash dump
        /// </summary>
        /// <param name="line">Line to check</param>
        /// <returns>True if this is the end of the crash dump</returns>
        private bool IsEndOfCrashDump(string line){
            // Look for ESP32 reboot/restart indicators
            if (line.Contains("ESP-ROM:") || 
                line.Contains("Build:") ||
                line.Contains("rst:0x") ||
                line.Contains("boot:0x") ||
                line.Contains("Chip revision:") ||
                line.Contains("ELF file SHA256:") ||
                line.Contains("Rebooting...")){
                return true;
            }
            
            // Check if we have a complete crash with backtrace
            var bufferContent = _crashBuffer.ToString();
            if (bufferContent.Contains("Backtrace:") && bufferContent.Length > 500){
                // If we have a backtrace and some substantial content, we can end capture
                // after a few more lines to ensure we get the complete backtrace
                var lines = bufferContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 10){
                    return true;
                }
            }
            
            // Prevent infinite buffer growth
            if (_crashBuffer.Length > 20000){
                _logger.LogWarning("Crash buffer size limit reached, ending capture");
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Completes crash capture and processes the crash
        /// </summary>
        /// <returns>True if crash was successfully processed</returns>
        private bool CompleteCrashCapture(){
            var crashData = _crashBuffer.ToString();
            
            _logger.LogCritical("🚨 ESP32 CRASH CAPTURE COMPLETE - PROCESSING CRASH DATA");
            
            // Mark crash as detected to prevent multiple detections
            _crashDetected = true;
            _isCapturingCrash = false;
            
            // Display the crash data
            Console.WriteLine("\n" + "="*80);
            Console.WriteLine("🚨 ESP32 CRASH DETECTED 🚨");
            Console.WriteLine("="*80);
            Console.WriteLine(crashData);
            Console.WriteLine("="*80);
            
            // Try to decode the crash using addr2line if available
            var decoded = DecodeException(crashData);
            
            if (!decoded){
                _logger.LogError("CRASH DECODING FAILED - SHOWING RAW CRASH DATA");
            }
            
            // Save crash data to file
            try{
                var crashFile = Path.Combine(_debugFilesPath, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                Directory.CreateDirectory(_debugFilesPath);
                File.WriteAllText(crashFile, crashData);
                Console.WriteLine($"Crash data saved to: {crashFile}");
            }
            catch (Exception ex){
                _logger.LogError(ex, "Failed to save crash data to file");
            }
            
            Console.WriteLine("\n🚨 ESP32 CRASH DETECTED - APPLICATION WILL EXIT 🚨");
            Console.WriteLine("Check output above for complete crash information\n");
            
            // Give time for output to flush
            System.Threading.Thread.Sleep(2000);
            
            // Force exit the application
            Environment.Exit(1);
            
            return true;
        }

        /// <summary>
        /// Checks if a line is a regular ESP32 debug message that should be ignored during crash detection
        /// </summary>
        /// <param name="line">Line to check</param>
        /// <returns>True if this is a regular debug message</returns>
        private bool IsRegularDebugMessage(string line){
            // ESP32 debug messages typically have format: [timestamp][level][file:line] message
            return line.StartsWith("[") && 
                   line.Contains("][") && 
                   (line.Contains(".cpp:") || line.Contains(".c:") || line.Contains(".h:")) &&
                   !line.Contains("panic") &&
                   !line.Contains("abort") &&
                   !line.Contains("exception") &&
                   !line.Contains("crash");
        }

        /// <summary>
        /// Decodes an ESP32 exception using addr2line if available
        /// </summary>
        /// <param name="crashData">Raw crash dump data</param>
        /// <returns>True if decoding was successful</returns>
        private bool DecodeException(string crashData){
            try{
                var elfPath = Path.Combine(_debugFilesPath, "firmware.elf");
                if (!File.Exists(elfPath)){
                    _logger.LogError("Firmware ELF file not found: {Path}", elfPath);
                    return false;
                }

                if (string.IsNullOrEmpty(_addr2linePath) || !File.Exists(_addr2linePath)){
                    _logger.LogError("addr2line tool not found: {Path}", _addr2linePath ?? "null");
                    return false;
                }

                // Extract addresses from the crash data
                var addresses = ExtractAddresses(crashData);
                if (addresses.Count == 0){
                    _logger.LogWarning("No addresses found in crash data");
                    return false;
                }

                Console.WriteLine("\n🔍 DECODED STACK TRACE:");
                Console.WriteLine("-" * 50);
                
                // Decode each address
                foreach (var address in addresses){
                    var decoded = DecodeAddress(address, elfPath);
                    if (!string.IsNullOrEmpty(decoded)){
                        Console.WriteLine($"0x{address:X8}: {decoded}");
                    }
                    else{
                        Console.WriteLine($"0x{address:X8}: (unable to decode)");
                    }
                }
                
                Console.WriteLine("-" * 50);
                return true;
            }
            catch (Exception ex){
                _logger.LogError(ex, "Error decoding exception");
                return false;
            }
        }

        /// <summary>
        /// Extract addresses from crash data
        /// </summary>
        /// <param name="crashData">Raw crash dump data</param>
        /// <returns>List of addresses found</returns>
        private List<uint> ExtractAddresses(string crashData){
            var addresses = new List<uint>();
            
            // Look for backtrace addresses
            var backtraceMatch = BacktraceRegex.Match(crashData);
            if (backtraceMatch.Success){
                var addressPattern = new Regex(@"0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
                var matches = addressPattern.Matches(backtraceMatch.Value);
                
                foreach (Match match in matches){
                    if (uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var address)){
                        addresses.Add(address);
                    }
                }
            }
            
            return addresses;
        }

        /// <summary>
        /// Decode a single address using addr2line
        /// </summary>
        /// <param name="address">Address to decode</param>
        /// <param name="elfPath">Path to ELF file</param>
        /// <returns>Decoded address information</returns>
        private string DecodeAddress(uint address, string elfPath){
            try{
                var startInfo = new ProcessStartInfo
                {
                    FileName = _addr2linePath,
                    Arguments = $"-e \"{elfPath}\" -f -C 0x{address:X8}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return string.Empty;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output)){
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length >= 2){
                        var function = lines[0].Trim();
                        var location = lines[1].Trim();
                        
                        if (function != "??" && location != "??:0"){
                            return $"{function} at {location}";
                        }
                    }
                }
                
                return string.Empty;
            }
            catch (Exception ex){
                _logger.LogTrace(ex, "Failed to decode address 0x{Address:X8}", address);
                return string.Empty;
            }
        }

        /// <summary>
        /// Reset crash detection state (for testing purposes)
        /// </summary>
        public void ResetCrashDetection(){
            _crashDetected = false;
            _isCapturingCrash = false;
            _crashBuffer.Clear();
            _lineBuffer.Clear();
            _crashStartTime = DateTime.MinValue;
        }
    }
} 
