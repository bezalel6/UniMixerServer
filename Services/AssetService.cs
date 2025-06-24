using System;
using System.Collections.Generic;
using System.IO;
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

        public AssetService(ILogger<AssetService> logger) {
            _logger = logger;
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

                // Try to load metadata first
                var metadata = await GetMetadataAsync(processName);
                if (metadata == null) {
                    response.ErrorMessage = $"No metadata found for process: {processName}";
                    return response;
                }

                // Load asset data
                var assetPath = GetAssetPath(processName, metadata.Format);
                if (!File.Exists(assetPath)) {
                    response.ErrorMessage = $"Asset file not found: {assetPath}";
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

                _logger.LogInformation($"Successfully loaded asset for process: {processName}");
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
                var metadataPath = GetMetadataPath(processName);
                if (!File.Exists(metadataPath)) {
                    return null;
                }

                var json = await File.ReadAllTextAsync(metadataPath);
                return JsonSerializer.Deserialize<LogoMetadata>(json);
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
    }
}
