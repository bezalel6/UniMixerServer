using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniMixerServer.Models;

namespace UniMixerServer.Services {
    public interface IAssetService {
        Task<AssetResponse> GetAssetAsync(string processName);
        Task<bool> StoreAssetAsync(string processName, byte[] assetData, LogoMetadata metadata);
        Task<LogoMetadata?> GetMetadataAsync(string processName);
    }

    public class AssetService : IAssetService {
        private readonly ILogger<AssetService> _logger;
        private readonly string _assetsDirectory;
        private readonly string _metadataDirectory;
        private readonly string _tempDirectory;
        private readonly IProcessIconExtractor? _iconExtractor;
        private readonly LogoFormat _logoFormat;

        public AssetService(ILogger<AssetService> logger, IProcessIconExtractor? iconExtractor = null, LogoFormat? logoFormat = null) {
            _logger = logger;
            _iconExtractor = iconExtractor;
            _logoFormat = logoFormat ?? new LogoFormat();
            _assetsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "assets");
            _metadataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "metadata");
            _tempDirectory = Path.Combine(Path.GetTempPath(), "UniMixer", "ImageConversion");

            // Ensure directories exist
            Directory.CreateDirectory(_assetsDirectory);
            Directory.CreateDirectory(_metadataDirectory);
            Directory.CreateDirectory(_tempDirectory);
        }

        public async Task<AssetResponse> GetAssetAsync(string processName) {
            try {
                var response = new AssetResponse {
                    MessageType = MessageType.ASSET_RESPONSE,
                    ProcessName = processName,
                    DeviceId = Environment.MachineName,
                    Success = false
                };

                // Try to find matching metadata using improved matching
                var (metadata, actualProcessName) = await FindMatchingMetadataAsync(processName);
                if (metadata == null) {
                    // response.ErrorMessage = $"No metadata found for process: {processName}";
                    // return response;
                    metadata = new LogoMetadata();
                    metadata.Format = _logoFormat.Format;
                }

                // Load asset data using the actual process name that was found
                var assetPath = GetAssetPath(actualProcessName, metadata.Format);
                if (!File.Exists(assetPath)) {
                    // Fallback: Try to extract icon dynamically
                    _logger.LogInformation($"No stored asset found for {processName}, attempting dynamic extraction");
                    var extractedAsset = await TryExtractProcessIconAsync(processName, _logoFormat.Format);
                    if (extractedAsset != null) {
                        response.Metadata = new LogoMetadata {
                            ProcessName = processName,
                            Format = _logoFormat.Format,
                            FileSize = (uint)extractedAsset.Length,
                            Checksum = CalculateMD5(extractedAsset),
                            ModifiedTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            UserFlags = new UserFlags { AutoDetected = true },
                            Width = (ushort)_logoFormat.Width,
                            Height = (ushort)_logoFormat.Height,
                            Version = 1
                        };
                        response.AssetData = extractedAsset;
                        response.Success = true;
                        _logger.LogInformation($"Successfully extracted icon for process: {processName}");
                        return response;
                    }

                    response.ErrorMessage = $"Asset file not found and dynamic extraction failed: {assetPath}";
                    return response;
                }

                var assetData = await File.ReadAllBytesAsync(assetPath);

                // Verify checksum
                var calculatedChecksum = CalculateMD5(assetData);
                if (calculatedChecksum != metadata.Checksum) {
                    response.ErrorMessage = "Asset checksum mismatch - file may be corrupted";
                    return response;
                }

                response.Metadata = metadata;
                response.AssetData = assetData;
                response.Success = true;

                _logger.LogInformation($"Successfully loaded asset for process: {processName} (matched: {actualProcessName})");
                return response;
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Error loading asset for process: {processName}");
                return new AssetResponse {
                    MessageType = MessageType.ASSET_RESPONSE,
                    ProcessName = processName,
                    DeviceId = Environment.MachineName,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<bool> StoreAssetAsync(string processName, byte[] assetData, LogoMetadata metadata) {
            try {
                // Update metadata with calculated values
                metadata.ProcessName = processName;
                metadata.FileSize = (uint)assetData.Length;
                metadata.Checksum = CalculateMD5(assetData);
                metadata.ModifiedTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Store asset file
                var assetPath = GetAssetPath(processName, metadata.Format);
                await File.WriteAllBytesAsync(assetPath, assetData);

                // Store metadata
                await StoreMetadataAsync(processName, metadata);

                _logger.LogInformation($"Successfully stored asset for process: {processName}");
                return true;
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Error storing asset for process: {processName}");
                return false;
            }
        }

        public async Task<LogoMetadata?> GetMetadataAsync(string processName) {
            try {
                var (metadata, _) = await FindMatchingMetadataAsync(processName);
                return metadata;
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Error loading metadata for process: {processName}");
                return null;
            }
        }

        private async Task StoreMetadataAsync(string processName, LogoMetadata metadata) {
            var metadataPath = GetMetadataPath(processName);
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metadataPath, json);
        }

        private string GetAssetPath(string processName, string format) {
            var sanitizedName = SanitizeFileName(processName);
            var extension = GetFileExtension(format);
            return Path.Combine(_assetsDirectory, $"{sanitizedName}.{extension}");
        }

        private string GetMetadataPath(string processName) {
            var sanitizedName = SanitizeFileName(processName);
            return Path.Combine(_metadataDirectory, $"{sanitizedName}.json");
        }

        private string SanitizeFileName(string fileName) {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars) {
                fileName = fileName.Replace(c, '_');
            }
            return fileName.ToLowerInvariant();
        }

        private string GetFileExtension(string format) {
            return format.ToLowerInvariant() switch {
                "lvgl_bin" => "bin",
                "lvgl_indexed" => "idx",
                "png" => "png",
                _ => "dat"
            };
        }

        private string CalculateMD5(byte[] data) {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private async Task<(LogoMetadata?, string)> FindMatchingMetadataAsync(string processName) {
            try {
                // Generate all possible variations of the process name to try
                var processVariations = GenerateProcessNameVariations(processName);

                _logger.LogInformation($"Searching for metadata for process: {processName}, trying {processVariations.Count} variations");

                foreach (var variation in processVariations) {
                    var metadataPath = GetMetadataPath(variation);
                    if (File.Exists(metadataPath)) {
                        var json = await File.ReadAllTextAsync(metadataPath);
                        var metadata = JsonSerializer.Deserialize<LogoMetadata>(json);
                        if (metadata != null) {
                            _logger.LogInformation($"Found metadata match: {processName} -> {variation}");
                            return (metadata, variation);
                        }
                    }
                }

                // If no direct file match, search through all metadata files for pattern matches
                if (Directory.Exists(_metadataDirectory)) {
                    var metadataFiles = Directory.GetFiles(_metadataDirectory, "*.json");
                    foreach (var metadataFile in metadataFiles) {
                        try {
                            var json = await File.ReadAllTextAsync(metadataFile);
                            var metadata = JsonSerializer.Deserialize<LogoMetadata>(json);
                            if (metadata != null && DoesProcessMatchPatterns(processName, metadata)) {
                                var fileProcessName = Path.GetFileNameWithoutExtension(metadataFile);
                                _logger.LogInformation($"Found pattern match: {processName} -> {fileProcessName}");
                                return (metadata, fileProcessName);
                            }
                        }
                        catch (Exception ex) {
                            _logger.LogWarning(ex, $"Error reading metadata file: {metadataFile}");
                        }
                    }
                }

                return (null, processName);
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Error finding matching metadata for process: {processName}");
                return (null, processName);
            }
        }

        private List<string> GenerateProcessNameVariations(string processName) {
            var variations = new List<string>();

            if (string.IsNullOrWhiteSpace(processName)) {
                return variations;
            }

            // Original name
            variations.Add(processName);

            // Case variations
            variations.Add(processName.ToLowerInvariant());
            variations.Add(processName.ToUpperInvariant());

            // With and without common extensions
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(processName);
            if (!string.Equals(nameWithoutExtension, processName, StringComparison.OrdinalIgnoreCase)) {
                variations.Add(nameWithoutExtension);
                variations.Add(nameWithoutExtension.ToLowerInvariant());
                variations.Add(nameWithoutExtension.ToUpperInvariant());
            }

            // Try adding common extensions if the name doesn't have one
            if (!Path.HasExtension(processName)) {
                var commonExtensions = new[] { ".exe", ".app", ".bin" };
                foreach (var ext in commonExtensions) {
                    variations.Add(processName + ext);
                    variations.Add(processName.ToLowerInvariant() + ext);
                }
            }

            // Remove duplicates while preserving order
            return variations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private bool DoesProcessMatchPatterns(string processName, LogoMetadata metadata) {
            if (string.IsNullOrWhiteSpace(metadata.Patterns)) {
                return false;
            }

            try {
                // Split comma-separated patterns and try each one
                var patterns = metadata.Patterns.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var nameWithoutExtension = Path.GetFileNameWithoutExtension(processName);

                foreach (var pattern in patterns) {
                    var trimmedPattern = pattern.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedPattern)) {
                        continue;
                    }

                    // Try exact match (case-insensitive)
                    if (string.Equals(processName, trimmedPattern, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(nameWithoutExtension, trimmedPattern, StringComparison.OrdinalIgnoreCase)) {
                        return true;
                    }

                    // Try regex match if the pattern looks like a regex
                    if (trimmedPattern.Contains('*') || trimmedPattern.Contains('?') ||
                        trimmedPattern.Contains('[') || trimmedPattern.Contains('(')) {
                        try {
                            // Convert simple wildcards to regex if needed
                            var regexPattern = trimmedPattern
                                .Replace("*", ".*")
                                .Replace("?", ".");

                            if (System.Text.RegularExpressions.Regex.IsMatch(processName, regexPattern,
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                                System.Text.RegularExpressions.Regex.IsMatch(nameWithoutExtension, regexPattern,
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
                                return true;
                            }
                        }
                        catch (Exception ex) {
                            _logger.LogWarning(ex, $"Invalid regex pattern in metadata: {trimmedPattern}");
                        }
                    }
                }
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, $"Error matching patterns for process: {processName}");
            }

            return false;
        }

        private async Task<byte[]?> TryExtractProcessIconAsync(string processName, string format) {
            _logger.LogInformation($"Starting dynamic icon extraction for process: {processName}");

            if (_iconExtractor == null) {
                _logger.LogWarning("ProcessIconExtractor not available for dynamic icon extraction");
                return null;
            }

            try {
                _logger.LogInformation($"Searching for running process with name: {processName}");

                // Find running processes by name
                var processId = FindProcessIdByName(processName);
                if (processId == null) {
                    _logger.LogWarning($"No running process found with name: {processName}");
                    _logger.LogInformation("This means the process is not currently running, so we cannot extract its icon");
                    return null;
                }

                _logger.LogInformation($"Found running process: {processName} (PID: {processId.Value})");
                _logger.LogInformation($"Attempting to extract icon using ProcessIconExtractor...");

                // Extract icon using ProcessIconExtractor
                var iconImage = await _iconExtractor.GetProcessIconImageAsync(processId.Value, processName);
                if (iconImage == null) {
                    _logger.LogWarning($"ProcessIconExtractor failed to extract icon for process: {processName} (PID: {processId.Value})");
                    _logger.LogWarning("This could be due to access permissions, missing executable, or other system-level issues");
                    return null;
                }



                _logger.LogInformation($"Successfully extracted icon for process: {processName}");

                // Convert image to required format  
                using (iconImage) {
                    _logger.LogInformation($"  Icon dimensions: {iconImage.Width}x{iconImage.Height}");
                    _logger.LogInformation($"  Icon pixel format: {iconImage.PixelFormat}");
                    _logger.LogInformation($"Now converting icon to LVGL format: {format}");

                    try {
                        var result = await ConvertImageToFormatAsync(iconImage, format);
                        _logger.LogInformation($"Successfully converted extracted icon to LVGL format ({result?.Length ?? 0} bytes)");
                        return result;
                    }
                    catch (ArgumentException ex) {
                        _logger.LogError($"Extracted icon for process '{processName}' is invalid or corrupted: {ex.Message}");
                        _logger.LogError("The icon extraction succeeded but the image data is corrupted");
                        return null;
                    }
                    catch (InvalidOperationException ex) {
                        _logger.LogError($"Failed to convert extracted icon for process '{processName}': {ex.Message}");
                        _logger.LogError("The icon extraction succeeded but LVGL conversion failed");
                        return null;
                    }
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Critical error during dynamic icon extraction for process: {processName}");
                return null;
            }
        }

        private int? FindProcessIdByName(string processName) {
            try {
                // Remove .exe extension if present
                var cleanProcessName = processName.Replace(".exe", "").ToLowerInvariant();

                var processes = Process.GetProcessesByName(cleanProcessName);
                if (processes.Length > 0) {
                    var processId = processes[0].Id;
                    // Dispose all process objects
                    foreach (var process in processes) {
                        process.Dispose();
                    }
                    return processId;
                }

                // Also try with original name in case it has no extension
                if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                    processes = Process.GetProcessesByName(processName);
                    if (processes.Length > 0) {
                        var processId = processes[0].Id;
                        // Dispose all process objects
                        foreach (var process in processes) {
                            process.Dispose();
                        }
                        return processId;
                    }
                }

                return null;
            }
            catch (Exception ex) {
                _logger.LogDebug(ex, $"Error finding process by name: {processName}");
                return null;
            }
        }

        private async Task<byte[]> ConvertImageToFormatAsync(Image image, string format) {
            // Validate image before processing
            if (image == null) {
                throw new ArgumentNullException(nameof(image), "Image cannot be null");
            }

            try {
                // Test if image is valid by accessing its properties
                var width = image.Width;
                var height = image.Height;
                var pixelFormat = image.PixelFormat;

                // Validate reasonable dimensions
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096) {
                    throw new ArgumentException($"Image has invalid dimensions: {width}x{height}");
                }
            }
            catch (Exception ex) {
                throw new ArgumentException($"Invalid or corrupted image: {ex.Message}", nameof(image), ex);
            }

            try {
                // Resize image to desired format size
                using (var resizedImage = ResizeImage(image, _logoFormat.Width, _logoFormat.Height)) {
                    if (format.ToLowerInvariant() == "png") {
                        return ConvertToPng(resizedImage);
                    }
                    else {
                        return await ConvertToLvglBinaryAsync(resizedImage, format);
                    }
                }
            }
            catch (Exception ex) {
                if (format.ToLowerInvariant() == "png") {
                    throw new InvalidOperationException($"Failed to convert image to PNG format: {ex.Message}.", ex);
                }
                else {
                    throw new InvalidOperationException($"Failed to convert image to LVGL binary format: {ex.Message}. Please ensure the LVGL image converter is properly installed (see README.md).", ex);
                }
            }
        }

        private async Task<byte[]> ConvertToLvglBinaryAsync(Image image, string format) {
            // Use permanent storage with descriptive names for debugging
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var debugId = Guid.NewGuid().ToString("N")[..6];
            var debugDirectory = Path.Combine(_assetsDirectory, "debug", "lvgl_conversion");
            Directory.CreateDirectory(debugDirectory);

            var inputFile = Path.Combine(debugDirectory, $"input_{timestamp}_{debugId}.png");
            var outputFile = Path.Combine(debugDirectory, $"output_{timestamp}_{debugId}.bin");

            _logger.LogInformation($"Starting LVGL conversion with permanent files for debugging:");
            _logger.LogInformation($"  Input file: {inputFile}");
            _logger.LogInformation($"  Expected output file: {outputFile}");
            _logger.LogInformation($"  Format: {format}");
            _logger.LogInformation($"  Image dimensions: {image.Width}x{image.Height}");
            _logger.LogInformation($"  Image pixel format: {image.PixelFormat}");

            try {
                // Save image as PNG for input to converter
                using (var bitmap = new Bitmap(image)) {
                    bitmap.Save(inputFile, ImageFormat.Png);
                    _logger.LogInformation($"Saved input image to: {inputFile} (size: {new FileInfo(inputFile).Length} bytes)");
                }

                // Call the official LVGL image converter (ts-node only, no fallbacks)
                var success = await CallLvglImageConverterAsync(inputFile, outputFile, format);
                if (!success) {
                    _logger.LogError($"LVGL image converter failed to process the image. Input file preserved at: {inputFile}");
                    throw new InvalidOperationException($"LVGL image converter failed to process the image. Debug files preserved at: {debugDirectory}");
                }

                // Read the converted binary file
                if (!File.Exists(outputFile)) {
                    _logger.LogError($"LVGL converter did not produce output file. Expected: {outputFile}");
                    _logger.LogError($"Input file preserved for debugging: {inputFile}");

                    // List all files in the debug directory for troubleshooting
                    var filesInDir = Directory.GetFiles(Path.GetDirectoryName(outputFile)!)
                        .Select(f => Path.GetFileName(f));
                    _logger.LogError($"Files in output directory: {string.Join(", ", filesInDir)}");

                    throw new InvalidOperationException($"LVGL converter did not produce output file. Debug files preserved at: {debugDirectory}");
                }

                var binaryData = await File.ReadAllBytesAsync(outputFile);
                _logger.LogInformation($"Successfully converted image using LVGL converter:");
                _logger.LogInformation($"  Output size: {binaryData.Length} bytes");
                _logger.LogInformation($"  Output file: {outputFile}");
                _logger.LogInformation($"  Debug files preserved for analysis");

                return binaryData;
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Error during LVGL conversion. Debug files preserved at: {debugDirectory}");
                throw;
            }
            // Note: NOT cleaning up files - preserving for debugging and issue replication
        }

        private async Task<bool> CallLvglImageConverterAsync(string inputFile, string outputFile, string format) {
            try {
                // Determine color format and output type based on format parameter
                var (colorFormat, outputType, binaryFormat) = GetLvglConverterParameters(format);

                _logger.LogInformation($"Preparing LVGL converter parameters:");
                _logger.LogInformation($"  Color format: {colorFormat}");
                _logger.LogInformation($"  Output type: {outputType}");
                _logger.LogInformation($"  Binary format: {binaryFormat}");

                // Build command arguments for the LVGL converter
                var arguments = $"\"{inputFile}\" -f -c {colorFormat} -t {outputType}";

                if (outputType == "bin" && !string.IsNullOrEmpty(binaryFormat)) {
                    arguments += $" --binary-format {binaryFormat}";
                }

                // Add output file specification - specify the exact output file, not just directory
                arguments += $" -o \"{outputFile}\"";

                _logger.LogInformation($"LVGL converter command arguments: {arguments}");

                // Execute ts-node only (no fallbacks)
                var success = await ExecuteTsNodeConverterAsync(arguments);

                if (success) {
                    _logger.LogInformation($"LVGL converter process completed successfully");
                    _logger.LogInformation($"Checking for output file: {outputFile}");

                    if (File.Exists(outputFile)) {
                        _logger.LogInformation($"✓ Output file created successfully: {outputFile}");
                        return true;
                    }
                    else {
                        _logger.LogError($"✗ Output file was not created: {outputFile}");

                        // List files in the output directory to see what was actually created
                        var outputDir = Path.GetDirectoryName(outputFile)!;
                        if (Directory.Exists(outputDir)) {
                            var filesInDir = Directory.GetFiles(outputDir).Select(f => Path.GetFileName(f));
                            _logger.LogInformation($"Files in output directory: {string.Join(", ", filesInDir)}");
                        }

                        return false;
                    }
                }

                _logger.LogError("LVGL converter process failed");
                return false;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Critical error calling LVGL image converter");
                return false;
            }
        }

        private async Task<bool> ExecuteTsNodeConverterAsync(string arguments) {
            var converterPath = GetLvglConverterPath();

            _logger.LogInformation($"Executing LVGL image converter:");
            _logger.LogInformation($"  Working directory: {converterPath}");
            _logger.LogInformation($"  Command: npx ts-node cli.ts {arguments}");

            try {
                var processInfo = new ProcessStartInfo {
                    FileName = "npx.cmd",
                    Arguments = $"ts-node cli.ts {arguments}",
                    WorkingDirectory = converterPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _logger.LogInformation($"Starting process: {processInfo.FileName}");
                _logger.LogInformation($"Process arguments: {processInfo.Arguments}");
                _logger.LogInformation($"Process working directory: {processInfo.WorkingDirectory}");

                using var process = new Process { StartInfo = processInfo };

                var startTime = DateTime.Now;
                process.Start();
                _logger.LogInformation($"Process started at {startTime:HH:mm:ss.fff}");

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                var endTime = DateTime.Now;
                var duration = endTime - startTime;

                _logger.LogInformation($"Process completed at {endTime:HH:mm:ss.fff} (duration: {duration.TotalMilliseconds:F0}ms)");
                _logger.LogInformation($"Process exit code: {process.ExitCode}");

                if (!string.IsNullOrWhiteSpace(stdout)) {
                    _logger.LogInformation($"Process stdout:");
                    foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                        _logger.LogInformation($"  STDOUT: {line.Trim()}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(stderr)) {
                    _logger.LogWarning($"Process stderr:");
                    foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                        _logger.LogWarning($"  STDERR: {line.Trim()}");
                    }
                }

                if (process.ExitCode == 0) {
                    _logger.LogInformation($"LVGL converter executed successfully (exit code 0)");
                    return true;
                }
                else {
                    _logger.LogError($"LVGL converter failed with exit code {process.ExitCode}");
                    _logger.LogError($"This indicates a problem with the lv_img_conv tool or its configuration");
                    return false;
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to execute npx ts-node LVGL converter");
                _logger.LogError($"Ensure Node.js and npx are installed and the lv_img_conv tool is properly set up");
                _logger.LogError($"Working directory was: {converterPath}");
                return false;
            }
        }



        private (string colorFormat, string outputType, string binaryFormat) GetLvglConverterParameters(string format) {
            return format.ToLowerInvariant() switch {
                "lvgl_bin" => ("CF_TRUE_COLOR_ALPHA", "c", "ARGB8565"),
                "lvgl_indexed" => ("CF_INDEXED_8_BIT", "c", "ARGB8565"),
                _ => ("CF_TRUE_COLOR_ALPHA", "c", "ARGB8565")
            };
        }

        private string GetLvglConverterPath() {
            _logger.LogInformation("Searching for LVGL converter in standard locations...");

            // Look for the LVGL converter in common locations
            var possiblePaths = new[] {
                Path.Combine(Directory.GetCurrentDirectory(), "tools", "lv_img_conv", "lib"),
                Path.Combine(Directory.GetCurrentDirectory(), "tools", "lv_img_conv\\lib"),
                Path.Combine(Directory.GetCurrentDirectory(), "lv_img_conv\\lib"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lv_img_conv\\lib"),
                "/usr/local/bin/lv_img_conv\\lib",
                "C:\\tools\\lv_img_conv\\lib"
            };

            _logger.LogInformation($"Checking {possiblePaths.Length} possible converter locations:");

            foreach (var path in possiblePaths) {
                _logger.LogInformation($"  Checking: {path}");

                if (Directory.Exists(path)) {
                    _logger.LogInformation($"    Directory exists: ✓");

                    var converterScript = Path.Combine(path, "cli.ts");
                    _logger.LogInformation($"    Looking for cli.ts at: {converterScript}");

                    if (File.Exists(converterScript)) {
                        _logger.LogInformation($"    Found cli.ts: ✓");
                        _logger.LogInformation($"LVGL converter found at: {path}");
                        return path;
                    }
                    else {
                        _logger.LogInformation($"    cli.ts not found: ✗");

                        // List what files are actually in the directory
                        try {
                            var filesInDir = Directory.GetFiles(path).Select(f => Path.GetFileName(f));
                            _logger.LogInformation($"    Files in directory: {string.Join(", ", filesInDir)}");
                        }
                        catch (Exception ex) {
                            _logger.LogWarning($"    Could not list files in directory: {ex.Message}");
                        }
                    }
                }
                else {
                    _logger.LogInformation($"    Directory does not exist: ✗");
                }
            }

            // Default to current directory - user needs to install the converter
            var currentDir = Directory.GetCurrentDirectory();
            _logger.LogError("LVGL converter not found in any standard locations!");
            _logger.LogError($"Please install lv_img_conv from https://github.com/lvgl/lv_img_conv");
            _logger.LogError($"Expected to find cli.ts in one of the checked locations");
            _logger.LogError($"Falling back to current directory: {currentDir}");

            return currentDir;
        }

        private byte[] ConvertToPng(Image image) {
            using (var memoryStream = new MemoryStream()) {
                image.Save(memoryStream, ImageFormat.Png);
                return memoryStream.ToArray();
            }
        }

        private Image ResizeImage(Image image, int width, int height) {
            if (image == null) {
                throw new ArgumentNullException(nameof(image), "Source image cannot be null");
            }

            if (width <= 0 || height <= 0) {
                throw new ArgumentException("Width and height must be positive values");
            }

            Bitmap? destImage = null;
            Graphics? graphics = null;

            try {
                destImage = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                graphics = Graphics.FromImage(destImage);

                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                // Clear the destination with transparent background
                graphics.Clear(Color.Transparent);

                // Draw the source image
                graphics.DrawImage(image, 0, 0, width, height);

                var result = destImage;
                destImage = null; // Prevent disposal in finally block
                return result;
            }
            catch (Exception ex) {
                throw new InvalidOperationException($"Failed to resize image from {image.Width}x{image.Height} to {width}x{height}: {ex.Message}", ex);
            }
            finally {
                graphics?.Dispose();
                destImage?.Dispose();
            }
        }
    }
}
