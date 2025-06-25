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
        private readonly IProcessIconExtractor? _iconExtractor;

        public AssetService(ILogger<AssetService> logger, IProcessIconExtractor? iconExtractor = null) {
            _logger = logger;
            _iconExtractor = iconExtractor;
            _assetsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "assets");
            _metadataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "metadata");

            // Ensure directories exist
            Directory.CreateDirectory(_assetsDirectory);
            Directory.CreateDirectory(_metadataDirectory);
        }

        public async Task<AssetResponse> GetAssetAsync(string processName) {
            try {
                var response = new AssetResponse {
                    MessageType = "AssetResponse",
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
                    metadata.Format = "lvgl_bin";
                }

                // Load asset data using the actual process name that was found
                var assetPath = GetAssetPath(actualProcessName, metadata.Format);
                if (!File.Exists(assetPath)) {
                    // Fallback: Try to extract icon dynamically
                    _logger.LogInformation($"No stored asset found for {processName}, attempting dynamic extraction");
                    var extractedAsset = await TryExtractProcessIconAsync(processName, metadata.Format);
                    if (extractedAsset != null) {
                        response.Metadata = new LogoMetadata {
                            ProcessName = processName,
                            Format = metadata.Format,
                            FileSize = (uint)extractedAsset.Length,
                            Checksum = CalculateMD5(extractedAsset),
                            ModifiedTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            UserFlags = new UserFlags { AutoDetected = true },
                            Width = 32, // Default icon size
                            Height = 32,
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
                    MessageType = "AssetResponse",
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
            if (_iconExtractor == null) {
                _logger.LogDebug("ProcessIconExtractor not available for dynamic icon extraction");
                return null;
            }

            try {
                // Find running processes by name
                var processId = FindProcessIdByName(processName);
                if (processId == null) {
                    _logger.LogDebug($"No running process found with name: {processName}");
                    return null;
                }

                // Extract icon using ProcessIconExtractor
                var iconImage = await _iconExtractor.GetProcessIconImageAsync(processId.Value, processName);
                if (iconImage == null) {
                    _logger.LogDebug($"Failed to extract icon for process: {processName}");
                    return null;
                }

                // Convert image to required format
                using (iconImage) {
                    try {
                        return ConvertImageToFormat(iconImage, format);
                    }
                    catch (ArgumentException ex) {
                        _logger.LogWarning($"Extracted icon for process '{processName}' is invalid or corrupted: {ex.Message}");
                        return null;
                    }
                    catch (InvalidOperationException ex) {
                        _logger.LogWarning($"Failed to convert icon for process '{processName}': {ex.Message}");
                        return null;
                    }
                }
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, $"Error during dynamic icon extraction for process: {processName}");
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

        private byte[] ConvertImageToFormat(Image image, string format) {
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

            // For now, convert to PNG format regardless of the requested format
            // In a real implementation, you might want to convert to specific formats like LVGL binary
            using (var memoryStream = new MemoryStream()) {
                try {
                    // Ensure image is 32x32 for consistency
                    using (var resizedImage = ResizeImage(image, 32, 32)) {
                        resizedImage.Save(memoryStream, ImageFormat.Png);
                    }
                    return memoryStream.ToArray();
                }
                catch (Exception ex) {
                    throw new InvalidOperationException($"Failed to convert image to format '{format}': {ex.Message}", ex);
                }
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
