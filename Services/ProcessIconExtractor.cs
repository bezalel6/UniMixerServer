using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UniMixerServer.Services{
    public interface IProcessIconExtractor{
        Task<string?> GetProcessIconPathAsync(int processId, string processName);
        Task<Image?> GetProcessIconImageAsync(int processId, string processName);
        void ClearCache();
        Task<Image?> GetDefaultIconAsync();
    }

    public class ProcessIconExtractor : IProcessIconExtractor{
        private readonly ILogger<ProcessIconExtractor> _logger;
        private readonly ConcurrentDictionary<string, string> _iconPathCache = new();
        private readonly ConcurrentDictionary<string, Image> _iconImageCache = new();
        private readonly string _iconCacheDirectory;
        private Image? _defaultIcon;

        // Windows API for icon extraction
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        public ProcessIconExtractor(ILogger<ProcessIconExtractor> logger){
            _logger = logger;
            _iconCacheDirectory = Path.Combine(Path.GetTempPath(), "UniMixer", "Icons");
            Directory.CreateDirectory(_iconCacheDirectory);
            
            // Create a default icon for fallback
            _defaultIcon = CreateDefaultIcon();
        }

        public async Task<string?> GetProcessIconPathAsync(int processId, string processName){
            try{
                var cacheKey = $"{processId}_{processName}";
                
                if (_iconPathCache.TryGetValue(cacheKey, out string? cachedPath) && File.Exists(cachedPath)){
                    return cachedPath;
                }

                var iconPath = await ExtractProcessIconAsync(processId, processName);
                if (!string.IsNullOrEmpty(iconPath)){
                    _iconPathCache[cacheKey] = iconPath;
                }

                return iconPath;
            }
            catch (Exception ex){
                _logger.LogError(ex, "Error getting icon path for process {ProcessId} ({ProcessName})", processId, processName);
                return null;
            }
        }

        public async Task<Image?> GetProcessIconImageAsync(int processId, string processName){
            try{
                var cacheKey = $"{processId}_{processName}";
                
                if (_iconImageCache.TryGetValue(cacheKey, out Image? cachedImage)){
                    return cachedImage;
                }

                var iconPath = await GetProcessIconPathAsync(processId, processName);
                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)){
                    var image = Image.FromFile(iconPath);
                    _iconImageCache[cacheKey] = image;
                    return image;
                }

                // Fallback to default icon
                return _defaultIcon;
            }
            catch (Exception ex){
                _logger.LogError(ex, "Error getting icon image for process {ProcessId} ({ProcessName})", processId, processName);
                return _defaultIcon;
            }
        }

        public async Task<Image?> GetDefaultIconAsync(){
            return await Task.FromResult(_defaultIcon);
        }

        private async Task<string?> ExtractProcessIconAsync(int processId, string processName){
            try{
                // Strategy 1: Try to get from running process (with access protection)
                try{
                    var process = Process.GetProcessById(processId);
                    if (!string.IsNullOrEmpty(process.MainModule?.FileName)){
                        var iconPath = await ExtractIconFromExecutableAsync(process.MainModule.FileName, processName);
                        if (!string.IsNullOrEmpty(iconPath)){
                            return iconPath;
                        }
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5){
                    // Access denied - this is expected for system processes, continue with other strategies
                    _logger.LogDebug("Access denied for process {ProcessId} ({ProcessName}), trying alternative methods", processId, processName);
                }
                catch (ArgumentException){
                    // Process has exited, continue with other strategies
                    _logger.LogDebug("Process {ProcessId} ({ProcessName}) has exited, trying alternative methods", processId, processName);
                }

                // Strategy 2: Try to find executable by process name
                var executablePath = FindExecutableByProcessName(processName);
                if (!string.IsNullOrEmpty(executablePath)){
                    var iconPath = await ExtractIconFromExecutableAsync(executablePath, processName);
                    if (!string.IsNullOrEmpty(iconPath)){
                        return iconPath;
                    }
                }

                // Strategy 3: Try common application directories
                var commonPaths = new[]{
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs")
                };

                foreach (var basePath in commonPaths){
                    var foundPath = await SearchForExecutableAsync(basePath, processName);
                    if (!string.IsNullOrEmpty(foundPath)){
                        var iconPath = await ExtractIconFromExecutableAsync(foundPath, processName);
                        if (!string.IsNullOrEmpty(iconPath)){
                            return iconPath;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex){
                _logger.LogDebug("Could not extract icon for process {ProcessId} ({ProcessName}): {Message}", processId, processName, ex.Message);
                return null;
            }
        }

        private async Task<string?> ExtractIconFromExecutableAsync(string executablePath, string processName){
            try{
                if (!File.Exists(executablePath)){
                    return null;
                }

                var iconFileName = $"{processName}_{Path.GetFileNameWithoutExtension(executablePath)}.png";
                var iconPath = Path.Combine(_iconCacheDirectory, iconFileName);

                if (File.Exists(iconPath)){
                    return iconPath;
                }

                var icon = await Task.Run(() => ExtractIconFromFile(executablePath));
                if (icon != null){
                    using (icon){
                        using (var bitmap = icon.ToBitmap()){
                            // Resize to standard size
                            using (var resized = ResizeImage(bitmap, 32, 32)){
                                resized.Save(iconPath, ImageFormat.Png);
                                return iconPath;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex){
                _logger.LogError(ex, "Error extracting icon from executable {ExecutablePath}", executablePath);
                return null;
            }
        }

        private Icon? ExtractIconFromFile(string filePath){
            try{
                // Try to extract large icon first
                var largeIcons = new IntPtr[1];
                var smallIcons = new IntPtr[1];

                int result = ExtractIconEx(filePath, 0, largeIcons, smallIcons, 1);
                if (result > 0){
                    try{
                        if (largeIcons[0] != IntPtr.Zero){
                            var icon = Icon.FromHandle(largeIcons[0]);
                            var clonedIcon = (Icon)icon.Clone();
                            DestroyIcon(largeIcons[0]);
                            return clonedIcon;
                        }
                        else if (smallIcons[0] != IntPtr.Zero){
                            var icon = Icon.FromHandle(smallIcons[0]);
                            var clonedIcon = (Icon)icon.Clone();
                            DestroyIcon(smallIcons[0]);
                            return clonedIcon;
                        }
                    }
                    finally{
                        if (largeIcons[0] != IntPtr.Zero) DestroyIcon(largeIcons[0]);
                        if (smallIcons[0] != IntPtr.Zero) DestroyIcon(smallIcons[0]);
                    }
                }

                // Fallback to ExtractIcon
                IntPtr hIcon = ExtractIcon(IntPtr.Zero, filePath, 0);
                if (hIcon != IntPtr.Zero && hIcon != new IntPtr(1)){
                    try{
                        var icon = Icon.FromHandle(hIcon);
                        var clonedIcon = (Icon)icon.Clone();
                        return clonedIcon;
                    }
                    finally{
                        DestroyIcon(hIcon);
                    }
                }

                return null;
            }
            catch (Exception ex){
                _logger.LogError(ex, "Error extracting icon from file {FilePath}", filePath);
                return null;
            }
        }

        private string? FindExecutableByProcessName(string processName){
            try{
                // Handle special system processes with known paths
                var systemProcessPaths = new Dictionary<string, string>{
                    {"svchost", Path.Combine(Environment.SystemDirectory, "svchost.exe")},
                    {"explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")},
                    {"winlogon", Path.Combine(Environment.SystemDirectory, "winlogon.exe")},
                    {"csrss", Path.Combine(Environment.SystemDirectory, "csrss.exe")},
                    {"lsass", Path.Combine(Environment.SystemDirectory, "lsass.exe")},
                    {"dwm", Path.Combine(Environment.SystemDirectory, "dwm.exe")}
                };

                var cleanProcessName = processName.Replace(".exe", "").ToLowerInvariant();
                if (systemProcessPaths.TryGetValue(cleanProcessName, out string? systemPath) && File.Exists(systemPath)){
                    return systemPath;
                }

                var processes = Process.GetProcessesByName(cleanProcessName);
                foreach (var process in processes){
                    try{
                        if (!string.IsNullOrEmpty(process.MainModule?.FileName)){
                            return process.MainModule.FileName;
                        }
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5){
                        // Access denied - expected for system processes
                        continue;
                    }
                    catch{
                        // Other errors - process may have exited
                        continue;
                    }
                    finally{
                        process.Dispose();
                    }
                }
                return null;
            }
            catch{
                return null;
            }
        }

        private async Task<string?> SearchForExecutableAsync(string basePath, string processName){
            try{
                if (!Directory.Exists(basePath)){
                    return null;
                }

                return await Task.Run(() =>{
                    var searchPatterns = new[]{
                        $"{processName}",
                        $"{processName}.exe",
                        $"{Path.GetFileNameWithoutExtension(processName)}.exe"
                    };

                    foreach (var pattern in searchPatterns){
                        var files = Directory.GetFiles(basePath, pattern, SearchOption.AllDirectories);
                        if (files.Length > 0){
                            return files[0];
                        }
                    }
                    return null;
                });
            }
            catch{
                return null;
            }
        }

        private Image ResizeImage(Image image, int width, int height){
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage)){
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes()){
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        private Image CreateDefaultIcon(){
            var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap)){
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(Brushes.DarkBlue, 4, 4, 24, 24);
                graphics.FillEllipse(Brushes.LightBlue, 8, 8, 16, 16);
                
                using (var font = new Font("Segoe UI", 8, FontStyle.Bold)){
                    graphics.DrawString("♪", font, Brushes.White, new PointF(11, 9));
                }
            }
            return bitmap;
        }

        public void ClearCache(){
            _iconPathCache.Clear();
            
            foreach (var image in _iconImageCache.Values){
                image?.Dispose();
            }
            _iconImageCache.Clear();

            try{
                if (Directory.Exists(_iconCacheDirectory)){
                    Directory.Delete(_iconCacheDirectory, true);
                    Directory.CreateDirectory(_iconCacheDirectory);
                }
            }
            catch (Exception ex){
                _logger.LogError(ex, "Error clearing icon cache directory");
            }
        }

        public void Dispose(){
            ClearCache();
            _defaultIcon?.Dispose();
        }
    }
} 
