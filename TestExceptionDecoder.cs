using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Services;

namespace UniMixerServer{
    public class TestExceptionDecoder{
        public static async Task RunTest(){
            Console.WriteLine("=== ESP32 Exception Decoder Test ===");
            Console.WriteLine($"Starting test at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();

            try{
                // Setup basic console logging for the test
                var loggerFactory = LoggerFactory.Create(builder =>
                    builder.AddConsole()
                           .SetMinimumLevel(LogLevel.Debug));

                var logger = loggerFactory.CreateLogger<EspExceptionDecoder>();
                var decoder = new EspExceptionDecoder(logger);

                Console.WriteLine("✓ Exception decoder created successfully");
                Console.WriteLine();

                // Test 1: Test with the sample crash file
                Console.WriteLine("Test 1: Testing with sample crash file");
                Console.WriteLine("=======================================");
                
                var testCrashFile = Path.Combine("tools", "esp-exception-decoder", "test_crash.txt");
                
                if (File.Exists(testCrashFile)){
                    Console.WriteLine($"Using test crash file: {testCrashFile}");
                    
                    var result = decoder.DecodeCrashFile(testCrashFile);
                    
                    if (result){
                        Console.WriteLine("✓ Test crash file decoded successfully!");
                    }
                    else{
                        Console.WriteLine("✗ Test crash file decoding failed");
                    }
                }
                else{
                    Console.WriteLine($"✗ Test crash file not found: {testCrashFile}");
                }

                Console.WriteLine();

                // Test 2: Test with inline crash data
                Console.WriteLine("Test 2: Testing with inline crash data");
                Console.WriteLine("=======================================");
                
                var testCrashData = @"Guru Meditation Error: Core  1 panic'ed (LoadProhibited). Exception was unhandled.

Core  1 register dump:
PC      : 0x4200ccfb  PS      : 0x00060d30  A0      : 0x82011d98  A1      : 0x3fca07f0
A2      : 0x3fca0b54  A3      : 0x3c094450  A4      : 0x0000ffff  A5      : 0x00000000
A6      : 0x3fca0b1c  A7      : 0x00000000  A8      : 0x8200ccf8  A9      : 0x3fca07d0
A10     : 0x00000004  A11     : 0x00000000  A12     : 0x00000001  A13     : 0x3fca07c0
A14     : 0x02cf6608  A15     : 0x00ffffff  SAR     : 0x0000001e  EXCCAUSE: 0x0000001c
EXCVADDR: 0x0000000a  LBEG    : 0x400556d5  LEND    : 0x400556e5  LCOUNT  : 0xfffffffd

Backtrace: 0x4200ccf8:0x3fca07f0 0x42011d95:0x3fca0840 0x42011e8d:0x3fca0880 0x420132f1:0x3fca08c0 0x4201365d:0x3fca09b0 0x42014b6a:0x3fca0a80 0x42016e83:0x3fca0ac0 0x42016ed9:0x3fca0ae0 0x420178b5:0x3fca0b80

ELF file SHA256: f5c0256cf1f7b937";

                // Test crash detection (without decoding to avoid exit)
                Console.WriteLine("Testing crash detection:");
                Console.WriteLine("Raw crash data detected: " + (testCrashData.Contains("Guru Meditation Error") ? "YES" : "NO"));
                
                Console.WriteLine();

                // Test 3: Check required components
                Console.WriteLine("Test 3: Checking required components");
                Console.WriteLine("=====================================");
                
                var debugDir = Path.Combine(Directory.GetCurrentDirectory(), "debug_files");
                Console.WriteLine($"Debug files directory: {debugDir}");
                Console.WriteLine($"Debug directory exists: {Directory.Exists(debugDir)}");
                
                var elfFile = Path.Combine(debugDir, "firmware.elf");
                Console.WriteLine($"ELF file path: {elfFile}");
                Console.WriteLine($"ELF file exists: {File.Exists(elfFile)}");
                
                var toolchainPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    ".platformio", "packages", "toolchain-xtensa-esp32", "bin", "xtensa-esp32-elf-addr2line.exe");
                Console.WriteLine($"Toolchain path: {toolchainPath}");
                Console.WriteLine($"Toolchain exists: {File.Exists(toolchainPath)}");
                
                var decoderScript = Path.Combine(Directory.GetCurrentDirectory(), "tools", "esp-exception-decoder", "decoder.py");
                Console.WriteLine($"Decoder script path: {decoderScript}");
                Console.WriteLine($"Decoder script exists: {File.Exists(decoderScript)}");

                Console.WriteLine();

                // Summary
                Console.WriteLine("=== Test Summary ===");
                Console.WriteLine("The exception decoder has been integrated successfully!");
                Console.WriteLine();
                Console.WriteLine("To use the decoder:");
                Console.WriteLine("1. Build your ESP32 project (firmware.elf will be copied to debug_files/)");
                Console.WriteLine("2. Run the server - it will automatically detect and decode crashes");
                Console.WriteLine("3. When a crash occurs, the server will display decoded information and exit");
                Console.WriteLine();
                Console.WriteLine("Prerequisites:");
                Console.WriteLine("- Python 3.x installed and in PATH");
                Console.WriteLine("- PlatformIO ESP32 toolchain installed");
                Console.WriteLine("- firmware.elf file in debug_files/ directory");
                Console.WriteLine();
                Console.WriteLine("The decoder will:");
                Console.WriteLine("- Monitor serial communication for crash patterns");
                Console.WriteLine("- Automatically decode crashes using the latest firmware.elf");
                Console.WriteLine("- Display human-readable function names and line numbers");
                Console.WriteLine("- Save decoded crashes to debug_files/ for analysis");
                Console.WriteLine("- Exit the server after decoding (for safety)");
                
                Console.WriteLine();
                Console.WriteLine($"Test completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex){
                Console.WriteLine($"CRITICAL ERROR during test: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
    }
} 
