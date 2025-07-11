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
                            _logger.LogInformation("🔧 Found ESP32-S3 toolchain at: {Path}", _toolchainPath);
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(_toolchainPath)) break;
                }
            }

            if (string.IsNullOrEmpty(_toolchainPath)){
                _logger.LogWarning("⚠️  ESP32-S3 toolchain not found. Exception decoding will be limited.");
                _logger.LogInformation("💡 To enable full exception decoding, install ESP-IDF or PlatformIO with ESP32-S3 support");
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
            
            _logger.LogTrace("🔍 ProcessBinaryData called with {Length} bytes", data.Length);
            
            try{
                // Convert binary data to string for pattern matching
                var dataString = Encoding.UTF8.GetString(data);
                _logger.LogTrace("🔍 Converted binary to string: {Length} chars - {Preview}", 
                    dataString.Length, 
                    dataString.Length > 100 ? dataString.Substring(0, 100) + "..." : dataString);
                return ProcessStringData(dataString);
            }
            catch (Exception ex){
                _logger.LogTrace(ex, "Failed to convert binary data to string for exception detection");
                
                // Try with different encodings if UTF8 fails
                try{
                    var dataString = Encoding.ASCII.GetString(data);
                    _logger.LogTrace("🔍 Converted binary to ASCII: {Length} chars - {Preview}", 
                        dataString.Length, 
                        dataString.Length > 100 ? dataString.Substring(0, 100) + "..." : dataString);
                    return ProcessStringData(dataString);
                }
                catch{
                    // If all encoding attempts fail, check for binary patterns
                    _logger.LogTrace("🔍 Both UTF8 and ASCII conversion failed, checking raw binary patterns");
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
            _logger.LogTrace("🔍 ProcessSerialData called with {Length} chars: {Preview}", 
                data.Length, 
                data.Length > 100 ? data.Substring(0, 100) + "..." : data);
            return ProcessStringData(data);
        }

        /// <summary>
        /// Processes a decoded JSON message to detect ESP32-S3 crashes embedded in messages
        /// </summary>
        /// <param name="jsonMessage">Decoded JSON message</param>
        /// <returns>True if a crash was detected and decoded, false otherwise</returns>
        public bool ProcessJsonMessage(string jsonMessage){
            if (string.IsNullOrEmpty(jsonMessage)) return false;

            _logger.LogTrace("🔍 ProcessJsonMessage called with: {Message}", jsonMessage);

            // Check if the JSON message contains crash information
            if (ContainsCrashIndicators(jsonMessage)){
                _logger.LogCritical("🚨 ESP32-S3 crash detected in JSON message");
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
            
            _logger.LogTrace("🔍 ProcessStringData called with {Length} chars: {Preview}", 
                data.Length, 
                data.Length > 200 ? data.Substring(0, 200) + "..." : data);
            
            // Check for crash timeout - if we've been capturing too long, force completion
            if (_isCapturingCrash && DateTime.UtcNow - _crashStartTime > TimeSpan.FromSeconds(MAX_CRASH_CAPTURE_SECONDS)){
                _logger.LogWarning("Crash capture timeout reached, completing capture");
                return CompleteCrashCapture();
            }

            var lines = data.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _logger.LogTrace("🔍 Split into {Count} lines", lines.Length);
            
            foreach (var line in lines){
                var trimmedLine = line.Trim();
                
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
                
                _logger.LogTrace("🔍 Processing line: {Line}", trimmedLine);
                
                // If we're already capturing a crash, continue adding data
                if (_isCapturingCrash){
                    _logger.LogTrace("🔍 Already capturing crash, adding line to buffer");
                    // Add all lines during crash capture to ensure we get everything
                    _crashBuffer.AppendLine(trimmedLine);
                    
                    // Check if this is the end of the crash dump
                    if (IsEndOfCrashDump(trimmedLine)){
                        _logger.LogTrace("🔍 End of crash dump detected");
                        return CompleteCrashCapture();
                    }
                    continue; // Continue processing crash data
                }
                
                // Only start new crash detection if we're not already processing one
                // and haven't already completed one
                if (!_crashDetected){
                    _logger.LogTrace("🔍 Checking for crash patterns in line: {Line}", trimmedLine);
                    
                    // Check for start of crash dump
                    if (GuruMeditationRegex.IsMatch(trimmedLine)){
                        _logger.LogCritical("🚨 ESP32-S3 GURU MEDITATION ERROR DETECTED: {Line}", trimmedLine);
                        StartCrashCapture(trimmedLine);
                        continue; // Continue processing to capture more data
                    }
                    
                    // Check for panic without full Guru Meditation
                    if (PanicRegex.IsMatch(trimmedLine) && !IsRegularDebugMessage(trimmedLine)){
                        _logger.LogCritical("🚨 ESP32-S3 PANIC DETECTED: {Line}", trimmedLine);
                        StartCrashCapture(trimmedLine);
                        continue; // Continue processing to capture more data
                    }
                    
                    _logger.LogTrace("🔍 No crash pattern found in line");
                }
                else{
                    _logger.LogTrace("🔍 Crash already detected, ignoring line");
                }
            }
            
            _logger.LogTrace("🔍 ProcessStringData completed, no crash detected");
            return false;
        }

        /// <summary>
        /// Starts capturing a crash dump and IMMEDIATELY halts normal processing
        /// </summary>
        /// <param name="initialLine">The first line of the crash</param>
        private void StartCrashCapture(string initialLine){
            _logger.LogCritical("🛑 HALTING ALL NORMAL PROCESSING - ESP32-S3 CRASH DETECTED");
            _isCapturingCrash = true;
            _crashStartTime = DateTime.UtcNow;
            _crashBuffer.Clear();
            _crashBuffer.AppendLine(initialLine);
            _logger.LogInformation("📝 Starting ESP32-S3 crash capture...");
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
                _logger.LogTrace("Backtrace line appears incomplete, continuing capture: {Line}", line);
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
                        _logger.LogTrace("Sufficient data captured after backtrace, ending capture");
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
            
            _logger.LogCritical("=====================================");
            _logger.LogCritical("🚨 ESP32-S3 CRASH DUMP CAPTURED");
            _logger.LogCritical("=====================================");
            _logger.LogCritical("📊 CRASH STATISTICS:");
            _logger.LogCritical("   Buffer Size: {Size} characters", crashData.Length);
            _logger.LogCritical("   Capture Duration: {Duration:F2} seconds", (DateTime.UtcNow - _crashStartTime).TotalSeconds);
            _logger.LogCritical("   Lines Captured: {Lines}", crashData.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            _logger.LogCritical("=====================================");
            _logger.LogCritical("📝 COMPLETE CRASH DATA:");
            _logger.LogCritical("=====================================");
            
            // Print crash data line by line for clarity
            var lines = crashData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++){
                _logger.LogCritical("[{LineNum:D3}] {Line}", i + 1, lines[i]);
            }
            
            _logger.LogCritical("=====================================");
            _logger.LogCritical("🔧 ATTEMPTING ESP32-S3 CRASH DECODE...");
            _logger.LogCritical("=====================================");
            
            // Mark crash as detected to prevent multiple detections
            _crashDetected = true;
            _isCapturingCrash = false;
            
            // Decode the crash using modern ESP-IDF tools
            var decoded = DecodeException(crashData);
            
            _logger.LogCritical("=====================================");
            if (decoded){
                _logger.LogCritical("✅ ESP32-S3 CRASH SUCCESSFULLY DECODED");
            }
            else{
                _logger.LogError("❌ ESP32-S3 CRASH DECODING FAILED");
                _logger.LogCritical("📄 Raw crash data is still available above");
            }
            _logger.LogCritical("=====================================");
            _logger.LogCritical("🛑 EXITING APPLICATION FOR DEBUGGING");
            _logger.LogCritical("=====================================");
            
            // Force console output and log flushing
            Console.WriteLine("=====================================");
            Console.WriteLine("🚨 ESP32-S3 CRASH DETECTED - APPLICATION EXITING");
            Console.WriteLine("Check logs above for complete crash dump and decode");
            Console.WriteLine("=====================================");
            
            // Give more time for logs to flush completely
            System.Threading.Thread.Sleep(3000);
            
            // Force exit the application
            _logger.LogCritical("🔥 FORCE EXITING APPLICATION NOW");
            Environment.Exit(1);
            
            return true; // This line will never be reached due to Environment.Exit
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
                    _logger.LogCritical("🚨 ESP32-S3 crash pattern detected in binary data");
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
                    _logger.LogError("Firmware ELF file not found at: {Path}", elfPath);
                    _logger.LogError("Make sure your ESP32-S3 build system uploads the firmware.elf file to debug_files/");
                    return false;
                }

                if (string.IsNullOrEmpty(_addr2linePath) || !File.Exists(_addr2linePath)){
                    _logger.LogError("ESP32-S3 addr2line tool not found at: {Path}", _addr2linePath ?? "null");
                    _logger.LogError("Install ESP-IDF with ESP32-S3 support to enable exception decoding");
                    return ManualDecoding(crashData);
                }

                _logger.LogInformation("🔧 Decoding ESP32-S3 crash using modern ESP-IDF tools:");
                _logger.LogInformation("   📁 ELF file: {ElfPath}", elfPath);
                _logger.LogInformation("   🛠️ addr2line: {Addr2linePath}", _addr2linePath);

                // Extract addresses from the crash data
                var addresses = ExtractAddresses(crashData);
                if (addresses.Count == 0){
                    _logger.LogWarning("No addresses found in crash data for decoding");
                    return ManualDecoding(crashData);
                }

                _logger.LogInformation("📍 Found {Count} addresses to decode", addresses.Count);

                // Decode each address
                var decodedLines = new List<string>();
                foreach (var address in addresses){
                    var decoded = DecodeAddress(address, elfPath);
                    if (!string.IsNullOrEmpty(decoded)){
                        decodedLines.Add($"0x{address:X8}: {decoded}");
                    }
                }

                if (decodedLines.Count > 0){
                    _logger.LogCritical("🎯 ESP32-S3 CRASH DECODED SUCCESSFULLY:");
                    _logger.LogCritical("=====================================");
                    foreach (var line in decodedLines){
                        _logger.LogCritical("{DecodedLine}", line);
                    }
                    _logger.LogCritical("=====================================");
                    
                    // Save decoded output to file
                    var decodedFile = Path.Combine(_debugFilesPath, $"decoded_crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    Directory.CreateDirectory(_debugFilesPath);
                    File.WriteAllText(decodedFile, $"ESP32-S3 Crash Decoded at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                                                   $"Original Crash:\n{crashData}\n\n" +
                                                   $"Decoded Backtrace:\n{string.Join("\n", decodedLines)}");
                    _logger.LogInformation("💾 Decoded crash saved to: {DecodedFile}", decodedFile);
                    
                    return true;
                }
                else{
                    _logger.LogWarning("No addresses could be decoded");
                    return ManualDecoding(crashData);
                }
            }
            catch (Exception ex){
                _logger.LogError(ex, "Exception occurred while decoding ESP32-S3 crash");
                return ManualDecoding(crashData);
            }
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
                    _logger.LogTrace("addr2line failed for 0x{Address:X8}: {Error}", address, error);
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
            _logger.LogCritical("🔍 ESP32-S3 MANUAL CRASH ANALYSIS:");
            _logger.LogCritical("=====================================");
            
            // Extract and analyze key registers
            var mepcMatch = Regex.Match(crashData, @"MEPC\s*:\s*0x([0-9a-fA-F]+)");
            if (mepcMatch.Success){
                _logger.LogCritical("📍 MEPC (Machine Exception Program Counter): 0x{Address}", mepcMatch.Groups[1].Value);
                _logger.LogCritical("   This is where the crash occurred");
            }

            var raMatch = Regex.Match(crashData, @"RA\s*:\s*0x([0-9a-fA-F]+)");
            if (raMatch.Success){
                _logger.LogCritical("📍 RA (Return Address): 0x{Address}", raMatch.Groups[1].Value);
                _logger.LogCritical("   This is where the crash came from");
            }

            var mcauseMatch = Regex.Match(crashData, @"MCAUSE\s*:\s*0x([0-9a-fA-F]+)");
            if (mcauseMatch.Success){
                var mcause = mcauseMatch.Groups[1].Value;
                _logger.LogCritical("📍 MCAUSE (Machine Cause): 0x{Cause}", mcause);
                _logger.LogCritical("   {CauseDescription}", GetMCauseDescription(mcause));
            }

            var mtvalMatch = Regex.Match(crashData, @"MTVAL\s*:\s*0x([0-9a-fA-F]+)");
            if (mtvalMatch.Success){
                _logger.LogCritical("📍 MTVAL (Machine Trap Value): 0x{Value}", mtvalMatch.Groups[1].Value);
                _logger.LogCritical("   This is the faulting address (for access faults)");
            }

            _logger.LogCritical("=====================================");
            _logger.LogCritical("💡 To get full function names and line numbers:");
            _logger.LogCritical("   1. Install ESP-IDF with ESP32-S3 support");
            _logger.LogCritical("   2. Use: riscv32-esp-elf-addr2line -pfiaC -e firmware.elf <address>");
            _logger.LogCritical("   3. Or use: idf.py monitor (automatically decodes crashes)");
            _logger.LogCritical("=====================================");

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
    }
} 
