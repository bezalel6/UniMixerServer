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
        private bool _isCapturingCrash = false;
        private bool _crashDetected = false; // Prevents multiple detections
        private DateTime _crashStartTime = DateTime.MinValue;
        private const int MAX_CRASH_CAPTURE_SECONDS = 3; // Maximum time to capture crash

        // ESP32-S3 (RISC-V) exception patterns
        private static readonly Regex GuruMeditationRegex = new Regex(
            @"Guru\s+Meditation\s+Error:\s+Core\s+(\d+)\s+panic'ed\s+\((.+?)\)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        private static readonly Regex BacktraceRegex = new Regex(
            @"Backtrace:\s*(0x[0-9a-fA-F]+:0x[0-9a-fA-F]+\s*)+", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        private static readonly Regex RegisterDumpRegex = new Regex(
            @"Core\s+\d+\s+register\s+dump:|MEPC\s*:\s*0x[0-9a-fA-F]+", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ESP32-S3 RISC-V crash patterns
        private static readonly Regex PanicRegex = new Regex(
            @"panic'ed|Load\s+access\s+fault|Store\s+access\s+fault|Instruction\s+access\s+fault|Illegal\s+instruction|abort\(\)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // RISC-V register patterns for ESP32-S3
        private static readonly Regex RiscVRegisterRegex = new Regex(
            @"(MEPC|RA|SP|GP|TP|T[0-6]|S[0-9]|A[0-7]|MSTATUS|MTVEC|MCAUSE|MTVAL|MHARTID)\s*:\s*0x([0-9a-fA-F]+)", 
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
                    // Find the actual toolchain directory (usually has version number)
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
                _logger.LogInformation("To enable full exception decoding, install ESP-IDF or PlatformIO with ESP32-S3 support");
            }
        }

        /// <summary>
        /// Check if crash detection is active - this should halt all normal processing
        /// </summary>
        public bool IsCrashDetectionActive => _isCapturingCrash || _crashDetected;

        /// <summary>
        /// Processes incoming binary data to detect ESP32-S3 crashes
        /// </summary>
        /// <param name="data">Raw binary data</param>
        /// <returns>True if a crash was detected and decoded, false otherwise</returns>
        public bool ProcessBinaryData(byte[] data){
            if (data == null || data.Length == 0) return false;
            
            try{
                // Convert binary data to string for pattern matching
                var dataString = Encoding.UTF8.GetString(data);
                return ProcessStringData(dataString);
            }
            catch (Exception ex){
                _logger.LogTrace(ex, "Failed to convert binary data to string for exception detection");
                
                // Try with different encodings if UTF8 fails
                try{
                    var dataString = Encoding.ASCII.GetString(data);
                    return ProcessStringData(dataString);
                }
                catch{
                    // If all encoding attempts fail, check for binary patterns
                    return ProcessRawBinaryData(data);
                }
            }
        }

        /// <summary>
        /// Processes incoming serial data to detect and decode ESP32-S3 crashes
        /// </summary>
        /// <param name="data">Raw serial data string</param>
        /// <returns>True if a crash was detected and decoded, false otherwise</returns>
        public bool ProcessSerialData(string data){
            return ProcessStringData(data);
        }

        /// <summary>
        /// Processes a decoded JSON message to detect ESP32-S3 crashes embedded in messages
        /// </summary>
        /// <param name="jsonMessage">Decoded JSON message</param>
        /// <returns>True if a crash was detected and decoded, false otherwise</returns>
        public bool ProcessJsonMessage(string jsonMessage){
            if (string.IsNullOrEmpty(jsonMessage)) return false;

            // Check if the JSON message contains crash information
            if (ContainsCrashIndicators(jsonMessage)){
                _logger.LogCritical("ESP32-S3 crash detected in JSON message");
                return ProcessStringData(jsonMessage);
            }

            return false;
        }

        /// <summary>
        /// Processes incoming string data to detect and decode ESP32-S3 crashes
        /// </summary>
        /// <param name="data">String data to process</param>
        /// <returns>True if a crash was detected and decoded, false otherwise</returns>
        private bool ProcessStringData(string data){
            if (string.IsNullOrEmpty(data)) return false;
            
            // Check for crash timeout - if we've been capturing too long, force completion
            if (_isCapturingCrash && DateTime.UtcNow - _crashStartTime > TimeSpan.FromSeconds(MAX_CRASH_CAPTURE_SECONDS)){
                _logger.LogWarning("Crash capture timeout reached, completing capture");
                return CompleteCrashCapture();
            }

            var lines = data.Split('\n');
            
            foreach (var line in lines){
                var trimmedLine = line.Trim();
                
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
                
                // If we're already capturing a crash, continue adding data
                if (_isCapturingCrash){
                    // Add all lines during crash capture to ensure we get everything
                    _crashBuffer.AppendLine(line);
                    
                    // Check if this is the end of the crash dump
                    if (IsEndOfCrashDump(line)){
                        return CompleteCrashCapture();
                    }
                    continue; // Continue processing crash data
                }
                
                // Only start new crash detection if we're not already processing one
                // and haven't already completed one
                if (!_crashDetected){
                    // Check for start of crash dump
                    if (GuruMeditationRegex.IsMatch(trimmedLine)){
                        _logger.LogCritical("ESP32-S3 GURU MEDITATION ERROR DETECTED: {Line}", trimmedLine);
                        StartCrashCapture(trimmedLine);
                        continue; // Continue processing to capture more data
                    }
                    
                    // Check for panic without full Guru Meditation
                    if (PanicRegex.IsMatch(trimmedLine) && !IsRegularDebugMessage(trimmedLine)){
                        _logger.LogCritical("ESP32-S3 PANIC DETECTED: {Line}", trimmedLine);
                        StartCrashCapture(trimmedLine);
                        continue; // Continue processing to capture more data
                    }
                }
            }
            
            return false;
        }

        /// <summary>
        /// Starts capturing a crash dump and IMMEDIATELY halts normal processing
        /// </summary>
        /// <param name="initialLine">The first line of the crash</param>
        private void StartCrashCapture(string initialLine){
            _logger.LogCritical("HALTING ALL NORMAL PROCESSING - ESP32-S3 CRASH DETECTED");
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
            // Look for definitive crash end markers
            if (line.StartsWith("ELF file SHA256:") || 
                line.Contains("Rebooting...") ||
                line.Contains("rst:0x") ||
                line.Contains("boot:0x") ||
                line.Contains("Build:") ||
                line.Contains("Chip revision:")){
                return true;
            }
            
            // Check for ESP32-S3 boot sequence start - indicates crash dump is complete
            if (line.Contains("ESP-ROM:esp32s3-") || 
                line.Contains("Build:Mar") ||
                line.Contains("rst:0x") ||
                line.Contains("boot:0x")){
                return true;
            }
            
            // For ESP32 classic crashes with backtraces, wait for more complete data
            var bufferContent = _crashBuffer.ToString();
            
            // If we have a backtrace line but it looks incomplete (too short), keep capturing
            if (line.Contains("Backtrace:") && line.Length < 50){
                return false;
            }
            
            // If we have both register dump and backtrace, and we've seen several lines after backtrace,
            // check if we have enough data to end
            if (bufferContent.Contains("register dump") && bufferContent.Contains("Backtrace:")){
                var lines = bufferContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var backtraceLineIndex = -1;
                
                // Find the backtrace line
                for (int i = 0; i < lines.Length; i++){
                    if (lines[i].Contains("Backtrace:")){
                        backtraceLineIndex = i;
                        break;
                    }
                }
                
                // If we have found backtrace and have captured several more lines after it,
                // or if we see empty lines after backtrace, we can end
                if (backtraceLineIndex >= 0){
                    var linesAfterBacktrace = lines.Length - backtraceLineIndex - 1;
                    if (linesAfterBacktrace >= 3 || (string.IsNullOrWhiteSpace(line) && linesAfterBacktrace >= 1)){
                        return true;
                    }
                }
            }
            
            // Prevent infinite buffer growth
            if (_crashBuffer.Length > 15000){
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
            
            // Only log essential crash detection info
            _logger.LogCritical("ESP32-S3 CRASH DETECTED");
            
            // Mark crash as detected to prevent multiple detections
            _crashDetected = true;
            _isCapturingCrash = false;
            
            // Decode the crash using modern ESP-IDF tools
            var decoded = DecodeException(crashData);
            
            if (!decoded){
                _logger.LogError("CRASH DECODING FAILED");
                // Show raw crash data if decoding failed
                Console.WriteLine("\nRAW CRASH DATA:");
                var lines = crashData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines){
                    Console.WriteLine(line.Trim());
                }
            }
            
            Console.WriteLine("\nESP32-S3 CRASH DETECTED - APPLICATION EXITING");
            Console.WriteLine("Check output above for complete crash information\n");
            
            // Give time for output to flush
            System.Threading.Thread.Sleep(1000);
            
            // Force exit the application
            Environment.Exit(1);
            
            return true;
        }

        /// <summary>
        /// Processes raw binary data looking for crash patterns
        /// </summary>
        /// <param name="data">Raw binary data</param>
        /// <returns>True if a crash was detected</returns>
        private bool ProcessRawBinaryData(byte[] data){
            if (_crashDetected) return false; // Already detected a crash
            
            // Look for common ESP32-S3 crash byte patterns
            var crashPatterns = new byte[][]{
                Encoding.ASCII.GetBytes("Guru Meditation"),
                Encoding.ASCII.GetBytes("panic'ed"),
                Encoding.ASCII.GetBytes("Load access fault"),
                Encoding.ASCII.GetBytes("Store access fault"),
                Encoding.ASCII.GetBytes("Instruction access fault"),
                Encoding.ASCII.GetBytes("MEPC")
            };

            foreach (var pattern in crashPatterns){
                if (ContainsBytePattern(data, pattern)){
                    _logger.LogCritical("ESP32-S3 crash pattern detected in binary data");
                    // Try to extract readable text around the pattern
                    var contextText = ExtractTextContext(data, pattern);
                    if (!string.IsNullOrEmpty(contextText)){
                        return ProcessStringData(contextText);
                    }
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the message contains crash indicators
        /// </summary>
        /// <param name="message">Message to check</param>
        /// <returns>True if crash indicators are found</returns>
        private bool ContainsCrashIndicators(string message){
            return GuruMeditationRegex.IsMatch(message) || 
                   PanicRegex.IsMatch(message);
        }

        /// <summary>
        /// Checks if a line is a regular ESP32-S3 debug message that should be ignored during crash detection
        /// </summary>
        /// <param name="line">Line to check</param>
        /// <returns>True if this is a regular debug message</returns>
        private bool IsRegularDebugMessage(string line){
            // ESP32-S3 debug messages typically have format: [timestamp][level][file:line] message
            return line.StartsWith("[") && 
                   line.Contains("][") && 
                   (line.Contains(".cpp:") || line.Contains(".c:") || line.Contains(".h:")) &&
                   !line.Contains("panic") &&
                   !line.Contains("abort") &&
                   !line.Contains("exception") &&
                   !line.Contains("crash");
        }

        /// <summary>
        /// Checks if binary data contains a specific byte pattern
        /// </summary>
        /// <param name="data">Binary data to search</param>
        /// <param name="pattern">Pattern to find</param>
        /// <returns>True if pattern is found</returns>
        private bool ContainsBytePattern(byte[] data, byte[] pattern){
            for (int i = 0; i <= data.Length - pattern.Length; i++){
                bool match = true;
                for (int j = 0; j < pattern.Length; j++){
                    if (data[i + j] != pattern[j]){
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

        /// <summary>
        /// Extracts readable text context around a found pattern
        /// </summary>
        /// <param name="data">Binary data</param>
        /// <param name="pattern">Pattern that was found</param>
        /// <returns>Readable text context</returns>
        private string ExtractTextContext(byte[] data, byte[] pattern){
            var patternIndex = -1;
            
            // Find the pattern in the data
            for (int i = 0; i <= data.Length - pattern.Length; i++){
                bool match = true;
                for (int j = 0; j < pattern.Length; j++){
                    if (data[i + j] != pattern[j]){
                        match = false;
                        break;
                    }
                }
                if (match){
                    patternIndex = i;
                    break;
                }
            }

            if (patternIndex == -1) return string.Empty;

            // Extract context around the pattern (500 bytes before and after)
            var contextStart = Math.Max(0, patternIndex - 500);
            var contextEnd = Math.Min(data.Length, patternIndex + pattern.Length + 500);
            var contextLength = contextEnd - contextStart;
            
            var contextBytes = new byte[contextLength];
            Array.Copy(data, contextStart, contextBytes, 0, contextLength);
            
            try{
                return Encoding.UTF8.GetString(contextBytes);
            }
            catch{
                return Encoding.ASCII.GetString(contextBytes);
            }
        }

        /// <summary>
        /// Decodes an ESP32-S3 exception using modern ESP-IDF tools
        /// </summary>
        /// <param name="crashData">Raw crash dump data</param>
        /// <returns>True if decoding was successful</returns>
        private bool DecodeException(string crashData){
            try{
                var elfPath = Path.Combine(_debugFilesPath, "firmware.elf");
                if (!File.Exists(elfPath)){
                    _logger.LogError("Firmware ELF file not found: {Path}", elfPath);
                    return ManualDecoding(crashData);
                }

                if (string.IsNullOrEmpty(_addr2linePath) || !File.Exists(_addr2linePath)){
                    _logger.LogError("addr2line tool not found: {Path}", _addr2linePath ?? "null");
                    return ManualDecoding(crashData);
                }

                // Extract addresses from the crash data
                var addresses = ExtractAddresses(crashData);
                _logger.LogInformation("Found {Count} addresses in crash data", addresses.Count);
                
                if (addresses.Count == 0){
                    _logger.LogWarning("No addresses found in crash data");
                    return ManualDecoding(crashData);
                }

                // Decode each address
                var decodedLines = new List<string>();
                foreach (var address in addresses){
                    var decoded = DecodeAddress(address, elfPath);
                    if (!string.IsNullOrEmpty(decoded)){
                        // Clean up the decoded line to remove duplicate address info
                        var cleanLine = decoded;
                        if (decoded.Contains(": 0x")){
                            var colonIndex = decoded.IndexOf(": ");
                            if (colonIndex > 0){
                                cleanLine = decoded.Substring(colonIndex + 2);
                            }
                        }
                        decodedLines.Add($"0x{address:X8}: {cleanLine}");
                    }
                }

                _logger.LogInformation("Successfully decoded {Count} addresses", decodedLines.Count);

                if (decodedLines.Count > 0){
                    // Display the crash with decoded stack trace
                    ShowCrashWithDecodedTrace(crashData, decodedLines);
                    
                    // Save decoded output to file
                    var decodedFile = Path.Combine(_debugFilesPath, $"decoded_crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    Directory.CreateDirectory(_debugFilesPath);
                    var crashWithDecoded = GetCrashWithDecodedTrace(crashData, decodedLines);
                    File.WriteAllText(decodedFile, $"ESP32-S3 Crash Decoded: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n{crashWithDecoded}");
                    
                    return true;
                }
                else{
                    _logger.LogWarning("No addresses could be decoded");
                    return ManualDecoding(crashData);
                }
            }
            catch (Exception ex){
                _logger.LogError(ex, "Exception while decoding crash");
                return ManualDecoding(crashData);
            }
        }
        
        /// <summary>
        /// Cleans up function names by removing template parameters and shortening paths
        /// </summary>
        /// <param name="decodedLine">Raw decoded line from addr2line</param>
        /// <returns>Cleaned up function name</returns>
        private string CleanupFunctionName(string decodedLine){
            if (string.IsNullOrEmpty(decodedLine)) return decodedLine;
            
            var cleaned = decodedLine;
            
            // Remove template parameters and namespace clutter
            cleaned = Regex.Replace(cleaned, @"ArduinoJson::V[0-9A-F]+::", "ArduinoJson::");
            cleaned = Regex.Replace(cleaned, @"<[^<>]*(?:<[^<>]*>[^<>]*)*>", "");
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            
            // Shorten extremely long function names
            if (cleaned.Length > 80){
                var parts = cleaned.Split(new[] { " at " }, StringSplitOptions.None);
                if (parts.Length == 2){
                    var funcName = parts[0].Trim();
                    var location = parts[1].Trim();
                    
                    // Simplify function name
                    if (funcName.Contains("::")){
                        var lastPart = funcName.Split(new[] { "::" }, StringSplitOptions.None).LastOrDefault();
                        if (!string.IsNullOrEmpty(lastPart) && lastPart.Length > 10){
                            funcName = "..." + lastPart;
                        }
                    }
                    
                    cleaned = $"{funcName} at {location}";
                }
            }
            
            return cleaned;
        }

        /// <summary>
        /// Shortens file paths to be more readable
        /// </summary>
        /// <param name="filePath">Full file path</param>
        /// <returns>Shortened path</returns>
        private string ShortenPath(string filePath){
            if (string.IsNullOrEmpty(filePath)) return filePath;
            
            // Extract just the filename and parent directory
            var parts = filePath.Replace('\\', '/').Split('/');
            if (parts.Length <= 2) return filePath;
            
            // Keep last 2-3 parts of the path
            var keepParts = Math.Min(3, parts.Length);
            return ".../" + string.Join("/", parts.Skip(parts.Length - keepParts));
        }

        /// <summary>
        /// Writes colored text to console
        /// </summary>
        /// <param name="text">Text to write</param>
        /// <param name="color">Color to use</param>
        /// <param name="newLine">Whether to add a new line</param>
        private void WriteColored(string text, ConsoleColor color, bool newLine = true){
            var originalColor = Console.ForegroundColor;
            try{
                Console.ForegroundColor = color;
                if (newLine){
                    Console.WriteLine(text);
                } else{
                    Console.Write(text);
                }
            }
            finally{
                Console.ForegroundColor = originalColor;
            }
        }

        /// <summary>
        /// Formats register dump with colors and better spacing
        /// </summary>
        /// <param name="line">Register dump line</param>
        private void DisplayRegisterLine(string line){
            if (string.IsNullOrEmpty(line)) return;
            
            // Check if this is a register line
            var registerMatch = Regex.Match(line, @"^([A-Z0-9]+)\s*:\s*(0x[0-9a-fA-F]+)(.*)$");
            if (registerMatch.Success){
                WriteColored("  ", ConsoleColor.White, false);
                WriteColored($"{registerMatch.Groups[1].Value,-8}", ConsoleColor.Yellow, false);
                WriteColored(": ", ConsoleColor.White, false);
                WriteColored($"{registerMatch.Groups[2].Value}", ConsoleColor.Cyan, false);
                WriteColored($"{registerMatch.Groups[3].Value}", ConsoleColor.Gray, true);
            } else{
                WriteColored($"  {line}", ConsoleColor.Gray);
            }
        }

        /// <summary>
        /// Displays the crash data with decoded stack trace replacing the raw backtrace
        /// </summary>
        /// <param name="crashData">Raw crash data</param>
        /// <param name="decodedLines">Decoded stack trace lines</param>
        private void ShowCrashWithDecodedTrace(string crashData, List<string> decodedLines){
            var lines = crashData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            _logger.LogInformation("ShowCrashWithDecodedTrace called with {Count} decoded lines", decodedLines.Count);
            
            Console.WriteLine();
            WriteColored("═══════════════════════════════════════════════════════════════════════════════", ConsoleColor.Red);
            WriteColored("                              ESP32 CRASH DETECTED                              ", ConsoleColor.Red);
            WriteColored("═══════════════════════════════════════════════════════════════════════════════", ConsoleColor.Red);
            Console.WriteLine();
            
            bool inRegisterDump = false;
            bool foundBacktrace = false;
            bool skipBacktraceLines = false;
            
            foreach (var line in lines){
                var trimmedLine = line.Trim();
                
                // Skip raw backtrace continuation lines after we've shown the decoded version
                if (skipBacktraceLines && trimmedLine.Contains("0x") && trimmedLine.Contains(":")){
                    continue;
                }
                
                // Handle Guru Meditation Error
                if (GuruMeditationRegex.IsMatch(trimmedLine)){
                    WriteColored("🔥 GURU MEDITATION ERROR:", ConsoleColor.Red);
                    var match = GuruMeditationRegex.Match(trimmedLine);
                    WriteColored($"   Core {match.Groups[1].Value} panic'ed: {match.Groups[2].Value}", ConsoleColor.Yellow);
                    Console.WriteLine();
                    continue;
                }
                
                // Handle panic reason
                if (trimmedLine.StartsWith("Debug exception reason:")){
                    WriteColored("📍 REASON:", ConsoleColor.Red);
                    WriteColored($"   {trimmedLine.Substring(22).Trim()}", ConsoleColor.Yellow);
                    Console.WriteLine();
                    continue;
                }
                
                // Handle register dump header
                if (trimmedLine.Contains("register dump:")){
                    inRegisterDump = true;
                    skipBacktraceLines = false;
                    WriteColored("📊 REGISTER DUMP:", ConsoleColor.Magenta);
                    continue;
                }
                
                // Handle register dump lines
                if (inRegisterDump && (trimmedLine.Contains(":") && trimmedLine.Contains("0x"))){
                    DisplayRegisterLine(trimmedLine);
                    continue;
                }
                
                // Handle backtrace
                if (trimmedLine.StartsWith("Backtrace:")){
                    foundBacktrace = true;
                    inRegisterDump = false;
                    skipBacktraceLines = true; // Start skipping raw backtrace lines
                    Console.WriteLine();
                    WriteColored("🔍 DECODED STACK TRACE:", ConsoleColor.Green);
                    
                    _logger.LogInformation("Found backtrace line, showing {Count} decoded addresses", decodedLines.Count);
                    
                    for (int i = 0; i < decodedLines.Count; i++){
                        var decodedLine = decodedLines[i];
                        
                        // Extract address and function info
                        var parts = decodedLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length == 2){
                            var address = parts[0];
                            var funcInfo = CleanupFunctionName(parts[1]);
                            
                            // Split function info into parts
                            var funcParts = funcInfo.Split(new[] { " at " }, 2, StringSplitOptions.None);
                            if (funcParts.Length == 2){
                                var funcName = funcParts[0].Trim();
                                var location = ShortenPath(funcParts[1].Trim());
                                
                                WriteColored($"  #{i,-2} ", ConsoleColor.White, false);
                                WriteColored($"{address} ", ConsoleColor.Cyan, false);
                                WriteColored($"{funcName}", ConsoleColor.Yellow);
                                WriteColored($"      └─ {location}", ConsoleColor.Gray);
                            } else{
                                WriteColored($"  #{i,-2} ", ConsoleColor.White, false);
                                WriteColored($"{address} ", ConsoleColor.Cyan, false);
                                WriteColored($"{funcInfo}", ConsoleColor.Yellow);
                            }
                        } else{
                            WriteColored($"  #{i,-2} {decodedLine}", ConsoleColor.Gray);
                        }
                    }
                    Console.WriteLine();
                    continue;
                }
                
                // Handle other lines (but not raw backtrace continuation lines)
                if (trimmedLine.StartsWith("Core") || trimmedLine.StartsWith("PC") || trimmedLine.StartsWith("ELF")){
                    inRegisterDump = false;
                    skipBacktraceLines = false;
                    WriteColored($"ℹ️  {trimmedLine}", ConsoleColor.Gray);
                } else if (!string.IsNullOrWhiteSpace(trimmedLine) && !skipBacktraceLines){
                    WriteColored($"   {trimmedLine}", ConsoleColor.Gray);
                }
            }
            
            if (!foundBacktrace){
                _logger.LogWarning("No backtrace line found in crash data, showing decoded addresses anyway");
                Console.WriteLine();
                WriteColored("🔍 DECODED STACK TRACE:", ConsoleColor.Green);
                
                for (int i = 0; i < decodedLines.Count; i++){
                    var decodedLine = decodedLines[i];
                    
                    // Extract address and function info
                    var parts = decodedLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2){
                        var address = parts[0];
                        var funcInfo = CleanupFunctionName(parts[1]);
                        
                        // Split function info into parts
                        var funcParts = funcInfo.Split(new[] { " at " }, 2, StringSplitOptions.None);
                        if (funcParts.Length == 2){
                            var funcName = funcParts[0].Trim();
                            var location = ShortenPath(funcParts[1].Trim());
                            
                            WriteColored($"  #{i,-2} ", ConsoleColor.White, false);
                            WriteColored($"{address} ", ConsoleColor.Cyan, false);
                            WriteColored($"{funcName}", ConsoleColor.Yellow);
                            WriteColored($"      └─ {location}", ConsoleColor.Gray);
                        } else{
                            WriteColored($"  #{i,-2} ", ConsoleColor.White, false);
                            WriteColored($"{address} ", ConsoleColor.Cyan, false);
                            WriteColored($"{funcInfo}", ConsoleColor.Yellow);
                        }
                    } else{
                        WriteColored($"  #{i,-2} {decodedLine}", ConsoleColor.Gray);
                    }
                }
                Console.WriteLine();
            }
            
            WriteColored("═══════════════════════════════════════════════════════════════════════════════", ConsoleColor.Red);
            Console.WriteLine();
        }
        
        /// <summary>
        /// Gets the crash data with decoded stack trace replacing the raw backtrace
        /// </summary>
        /// <param name="crashData">Raw crash data</param>
        /// <param name="decodedLines">Decoded stack trace lines</param>
        /// <returns>Formatted crash data with decoded trace</returns>
        private string GetCrashWithDecodedTrace(string crashData, List<string> decodedLines){
            var lines = crashData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new StringBuilder();
            
            result.AppendLine("ESP32 CRASH DECODED");
            result.AppendLine("==================");
            result.AppendLine();
            
            foreach (var line in lines){
                var trimmedLine = line.Trim();
                
                // Replace backtrace with decoded stack trace
                if (trimmedLine.StartsWith("Backtrace:")){
                    result.AppendLine("DECODED STACK TRACE:");
                    result.AppendLine("-------------------");
                    
                    for (int i = 0; i < decodedLines.Count; i++){
                        var decodedLine = decodedLines[i];
                        var parts = decodedLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length == 2){
                            var address = parts[0];
                            var funcInfo = CleanupFunctionName(parts[1]);
                            
                            var funcParts = funcInfo.Split(new[] { " at " }, 2, StringSplitOptions.None);
                            if (funcParts.Length == 2){
                                var funcName = funcParts[0].Trim();
                                var location = ShortenPath(funcParts[1].Trim());
                                
                                result.AppendLine($"  #{i:D2} {address} {funcName}");
                                result.AppendLine($"      └─ {location}");
                            } else{
                                result.AppendLine($"  #{i:D2} {address} {funcInfo}");
                            }
                        } else{
                            result.AppendLine($"  #{i:D2} {decodedLine}");
                        }
                    }
                    result.AppendLine();
                } else{
                    result.AppendLine(trimmedLine);
                }
            }
            
            return result.ToString();
        }

        /// <summary>
        /// Extracts addresses from ESP32/ESP32-S3 crash data for decoding
        /// </summary>
        /// <param name="crashData">Raw crash data</param>
        /// <returns>List of addresses found</returns>
        private List<uint> ExtractAddresses(string crashData){
            var addresses = new List<uint>();
            
            // ESP32-S3 RISC-V format: Look for MEPC (Machine Exception Program Counter) - main crash address
            var mepcMatch = Regex.Match(crashData, @"MEPC\s*:\s*0x([0-9a-fA-F]+)");
            if (mepcMatch.Success){
                if (uint.TryParse(mepcMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var mepc)){
                 
                    addresses.Add(mepc);
                }
            }

            // ESP32-S3 RISC-V format: Look for RA (Return Address) - shows where the crash came from
            var raMatch = Regex.Match(crashData, @"RA\s*:\s*0x([0-9a-fA-F]+)");
            if (raMatch.Success){
                if (uint.TryParse(raMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var ra)){
                    addresses.Add(ra);
                }
            }

            // ESP32 Classic Xtensa format: Look for PC (Program Counter) - main crash address
            var pcMatch = Regex.Match(crashData, @"PC\s*:\s*0x([0-9a-fA-F]+)");
            if (pcMatch.Success){
                if (uint.TryParse(pcMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var pc)){
                    addresses.Add(pc);
                }
            }

            // ESP32 Classic Xtensa format: Look for A0 register (return address)
            var a0Match = Regex.Match(crashData, @"A0\s*:\s*0x([0-9a-fA-F]+)");
            if (a0Match.Success){
                if (uint.TryParse(a0Match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var a0)){
                    addresses.Add(a0);
                }
            }

            // Look for backtrace addresses - ESP32 classic format: 0x40123456:0x3ff12345
            var backtraceMatches = Regex.Matches(crashData, @"0x([0-9a-fA-F]+):0x([0-9a-fA-F]+)");
            foreach (Match match in backtraceMatches){
                if (uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var addr)){
                    addresses.Add(addr);
                }
            }

            // Look for standalone backtrace addresses - sometimes they appear without pairs
            // Extract all addresses from backtrace lines
            var backtraceLines = crashData.Split('\n')
                .Where(line => line.Contains("Backtrace:"))
                .ToList();
            
            foreach (var line in backtraceLines){
                var standaloneMatches = Regex.Matches(line, @"0x([0-9a-fA-F]+)");
                foreach (Match match in standaloneMatches){
                    if (uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var addr)){
                        // Only add if it looks like a code address (starts with 4, 5, or 6)
                        if ((addr & 0xF0000000) == 0x40000000 || 
                            (addr & 0xF0000000) == 0x50000000 || 
                            (addr & 0xF0000000) == 0x60000000){
                            addresses.Add(addr);
                        }
                    }
                }
            }

            // Look for stack memory addresses that might be function pointers
            var stackMatches = Regex.Matches(crashData, @"0x([4-6][0-9a-fA-F]{7})");
            foreach (Match match in stackMatches){
                if (uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var addr)){
                    // Only add if it looks like a code address (starts with 4, 5, or 6)
                    if ((addr & 0xF0000000) == 0x40000000 || 
                        (addr & 0xF0000000) == 0x50000000 || 
                        (addr & 0xF0000000) == 0x60000000){
                        addresses.Add(addr);
                    }
                }
            }

            return addresses.Distinct().ToList();
        }

        /// <summary>
        /// Decodes a single address using addr2line
        /// </summary>
        /// <param name="address">Address to decode</param>
        /// <param name="elfPath">Path to ELF file</param>
        /// <returns>Decoded function and line information</returns>
        private string DecodeAddress(uint address, string elfPath){
            try{
                var processInfo = new ProcessStartInfo{
                    FileName = _addr2linePath,
                    Arguments = $"-pfiaC -e \"{elfPath}\" 0x{address:X8}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process == null) return string.Empty;

                process.WaitForExit(5000); // 5 second timeout per address

                var output = process.StandardOutput.ReadToEnd().Trim();
                var error = process.StandardError.ReadToEnd().Trim();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && !output.Contains("??:0")){
                    return output;
                }
                else{
                    return string.Empty;
                }
            }
            catch (Exception ex){
                _logger.LogTrace(ex, "Failed to decode address 0x{Address:X8}", address);
                return string.Empty;
            }
        }

        /// <summary>
        /// Provides manual decoding when automated tools are not available
        /// </summary>
        /// <param name="crashData">Raw crash data</param>
        /// <returns>True if manual decoding was provided</returns>
        private bool ManualDecoding(string crashData){
            // Show the crash data with improved formatting
            Console.WriteLine();
            WriteColored("═══════════════════════════════════════════════════════════════════════════════", ConsoleColor.Red);
            WriteColored("                              ESP32 CRASH DETECTED                              ", ConsoleColor.Red);
            WriteColored("═══════════════════════════════════════════════════════════════════════════════", ConsoleColor.Red);
            Console.WriteLine();
            
            var lines = crashData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool inRegisterDump = false;
            bool inBacktrace = false;
            
            foreach (var line in lines){
                var trimmedLine = line.Trim();
                
                // Handle Guru Meditation Error
                if (GuruMeditationRegex.IsMatch(trimmedLine)){
                    WriteColored("🔥 GURU MEDITATION ERROR:", ConsoleColor.Red);
                    var match = GuruMeditationRegex.Match(trimmedLine);
                    WriteColored($"   Core {match.Groups[1].Value} panic'ed: {match.Groups[2].Value}", ConsoleColor.Yellow);
                    Console.WriteLine();
                    continue;
                }
                
                // Handle panic reason
                if (trimmedLine.StartsWith("Debug exception reason:")){
                    WriteColored("📍 REASON:", ConsoleColor.Red);
                    WriteColored($"   {trimmedLine.Substring(22).Trim()}", ConsoleColor.Yellow);
                    Console.WriteLine();
                    continue;
                }
                
                // Handle register dump
                if (trimmedLine.Contains("register dump:")){
                    inRegisterDump = true;
                    inBacktrace = false;
                    WriteColored("📊 REGISTER DUMP:", ConsoleColor.Magenta);
                    continue;
                }
                
                if (inRegisterDump && (trimmedLine.Contains(":") && trimmedLine.Contains("0x"))){
                    DisplayRegisterLine(trimmedLine);
                    continue;
                }
                
                // Handle backtrace - check for both the start and continuation lines
                if (trimmedLine.StartsWith("Backtrace:") || (inBacktrace && trimmedLine.Contains("0x"))){
                    // First backtrace line
                    if (trimmedLine.StartsWith("Backtrace:")){
                        inRegisterDump = false;
                        inBacktrace = true;
                        Console.WriteLine();
                        WriteColored("🔍 RAW BACKTRACE:", ConsoleColor.Green);
                        
                        // Extract addresses from the backtrace line
                        var backtraceAddresses = ExtractBacktraceAddresses(trimmedLine);
                        WriteColored($"   Found {backtraceAddresses.Count} addresses in backtrace", ConsoleColor.Gray);
                        
                        // Show a few addresses for reference
                        if (backtraceAddresses.Count > 0){
                            WriteColored("   Key addresses:", ConsoleColor.Gray);
                            for (int i = 0; i < Math.Min(5, backtraceAddresses.Count); i++){
                                WriteColored($"     #{i}: 0x{backtraceAddresses[i]:X8}", ConsoleColor.Cyan);
                            }
                            if (backtraceAddresses.Count > 5){
                                WriteColored($"     ... and {backtraceAddresses.Count - 5} more", ConsoleColor.Gray);
                            }
                        }
                        continue;
                    }
                    // Continuation backtrace line
                    else if (inBacktrace && trimmedLine.Contains("0x")){
                        continue; // Skip continuation lines, already handled above
                    }
                }
                
                // If we were in backtrace and hit a non-backtrace line, end backtrace mode
                if (inBacktrace && !trimmedLine.Contains("0x")){
                    inBacktrace = false;
                }
                
                // Handle other lines
                if (trimmedLine.StartsWith("Core") || trimmedLine.StartsWith("PC") || trimmedLine.StartsWith("ELF")){
                    inRegisterDump = false;
                    inBacktrace = false;
                    WriteColored($"ℹ️  {trimmedLine}", ConsoleColor.Gray);
                } else if (!string.IsNullOrWhiteSpace(trimmedLine) && !inBacktrace){
                    WriteColored($"   {trimmedLine}", ConsoleColor.Gray);
                }
            }
            
            // Show manual analysis with colors
            Console.WriteLine();
            WriteColored("🔧 MANUAL ANALYSIS:", ConsoleColor.Yellow);
            
            // Extract and analyze key registers
            var mepcMatch = Regex.Match(crashData, @"MEPC\s*:\s*0x([0-9a-fA-F]+)");
            if (mepcMatch.Success){
                WriteColored("  🎯 CRASH LOCATION:", ConsoleColor.Cyan);
                WriteColored($"     MEPC: 0x{mepcMatch.Groups[1].Value} (Machine Exception Program Counter)", ConsoleColor.White);
                WriteColored("     → This is where the crash occurred", ConsoleColor.Gray);
            }

            var raMatch = Regex.Match(crashData, @"RA\s*:\s*0x([0-9a-fA-F]+)");
            if (raMatch.Success){
                WriteColored("  🔙 RETURN ADDRESS:", ConsoleColor.Cyan);
                WriteColored($"     RA: 0x{raMatch.Groups[1].Value} (Return Address)", ConsoleColor.White);
                WriteColored("     → This is where the crash came from", ConsoleColor.Gray);
            }

            var mcauseMatch = Regex.Match(crashData, @"MCAUSE\s*:\s*0x([0-9a-fA-F]+)");
            if (mcauseMatch.Success){
                var mcause = mcauseMatch.Groups[1].Value;
                WriteColored("  ⚠️  CRASH CAUSE:", ConsoleColor.Cyan);
                WriteColored($"     MCAUSE: 0x{mcause} (Machine Cause)", ConsoleColor.White);
                WriteColored($"     → {GetMCauseDescription(mcause)}", ConsoleColor.Gray);
            }

            var mtvalMatch = Regex.Match(crashData, @"MTVAL\s*:\s*0x([0-9a-fA-F]+)");
            if (mtvalMatch.Success){
                WriteColored("  📍 FAULT ADDRESS:", ConsoleColor.Cyan);
                WriteColored($"     MTVAL: 0x{mtvalMatch.Groups[1].Value} (Machine Trap Value)", ConsoleColor.White);
                WriteColored("     → This is the faulting address (for access faults)", ConsoleColor.Gray);
            }

            Console.WriteLine();
            WriteColored("💡 TO GET FULL FUNCTION NAMES AND LINE NUMBERS:", ConsoleColor.Yellow);
            WriteColored("   1. Install ESP-IDF with ESP32-S3 support", ConsoleColor.Gray);
            WriteColored("   2. Use: riscv32-esp-elf-addr2line -pfiaC -e firmware.elf <address>", ConsoleColor.Gray);
            WriteColored("   3. Or use: idf.py monitor (automatically decodes crashes)", ConsoleColor.Gray);
            
            WriteColored("═══════════════════════════════════════════════════════════════════════════════", ConsoleColor.Red);
            Console.WriteLine();

            return true;
        }

        /// <summary>
        /// Gets description for RISC-V MCAUSE register values
        /// </summary>
        /// <param name="mcauseHex">MCAUSE value in hex</param>
        /// <returns>Description of the cause</returns>
        private string GetMCauseDescription(string mcauseHex){
            if (uint.TryParse(mcauseHex, System.Globalization.NumberStyles.HexNumber, null, out var mcause)){
                return mcause switch{
                    0x00000001 => "Instruction access fault",
                    0x00000002 => "Illegal instruction",
                    0x00000003 => "Breakpoint",
                    0x00000004 => "Load address misaligned",
                    0x00000005 => "Load access fault",
                    0x00000006 => "Store/AMO address misaligned",
                    0x00000007 => "Store/AMO access fault",
                    0x00000008 => "Environment call from U-mode",
                    0x00000009 => "Environment call from S-mode",
                    0x0000000B => "Environment call from M-mode",
                    0x0000000C => "Instruction page fault",
                    0x0000000D => "Load page fault",
                    0x0000000F => "Store/AMO page fault",
                    _ => $"Unknown or custom cause (0x{mcause:X8})"
                };
            }
            return "Invalid MCAUSE value";
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

        /// <summary>
        /// Reset the crash detection state (for testing)
        /// </summary>
        public void ResetCrashDetection(){
            _crashDetected = false;
            _isCapturingCrash = false;
            _crashBuffer.Clear();
        }

        /// <summary>
        /// Extracts addresses from a backtrace line
        /// </summary>
        /// <param name="backtraceLine">The backtrace line</param>
        /// <returns>List of addresses found</returns>
        private List<uint> ExtractBacktraceAddresses(string backtraceLine){
            var addresses = new List<uint>();
            var matches = Regex.Matches(backtraceLine, @"0x([0-9a-fA-F]+)");
            
            foreach (Match match in matches){
                if (uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var addr)){
                    // Only add if it looks like a code address (starts with 4, 5, or 6)
                    if ((addr & 0xF0000000) == 0x40000000 || 
                        (addr & 0xF0000000) == 0x50000000 || 
                        (addr & 0xF0000000) == 0x60000000){
                        addresses.Add(addr);
                    }
                }
            }
            
            return addresses;
        }
    }
} 
