using System;
using System.Text.RegularExpressions;
using DryDock.Configuration;
using DryDock.Contract;
using DryDock.Interop;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Storage
{

    public partial class LocalStorageManager : IStorageManager
    {
        private readonly DryDockOptions _options;
        private readonly ILogger _logger;
        private readonly string _storageRoot;

        [GeneratedRegex("^[a-zA-Z0-9_.-]+$", RegexOptions.Compiled)]
        private static partial Regex SafeVolumeIdRegex();

        public LocalStorageManager(IOptions<DryDockOptions> options, ILogger<LocalStorageManager> logger)
        {
            _options = options.Value;
            _logger = logger;

            _storageRoot = Path.GetFullPath(_options.LocalStorageRoot);
            if (!Directory.Exists(_storageRoot))
            {
                Directory.CreateDirectory(_storageRoot);
                _logger.LogInformation("Drydock Storage Root directory initialized: {Root}", _storageRoot);
            }
        }

        public string GetVolumeDirectory(string volumeId)
        {
            ValidateVolumeId(volumeId);
            var combinedPath = Path.Combine(_storageRoot, volumeId);
            var fullPath = Path.GetFullPath(combinedPath);

            if (!fullPath.StartsWith(_storageRoot, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Illegal Path Access for VolumeId '{volumeId}' detected.");
            }

            return fullPath;
        }

        public bool ExistsAndHasData(string volumeId)
        {
            var path = GetVolumeDirectory(volumeId);
            if (!Directory.Exists(path))
            {
                return false;
            }

            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            return enumerator.MoveNext();
        }

        public void EnsureVolumeDirectoryCreated(string volumeId)
        {
            var path = GetVolumeDirectory(volumeId);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logger.LogInformation("Directory for volume {VolumeId} created: {Path}", volumeId, path);
            }
        }

        public void DeleteVolumeDirectory(string volumeId)
        {
            var path = GetVolumeDirectory(volumeId);
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                var trashDir = Path.Combine(_storageRoot, $".trash-{volumeId}-{Guid.NewGuid():N}");
                Directory.Move(path, trashDir);

                Task.Run(() =>
                {
                    try
                    {
                        Directory.Delete(trashDir, recursive: true);
                        _logger.LogInformation("Volume {VolumeId} permanently cleaned.", volumeId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error while cleaning the trash directory {Trash}", trashDir);
                    }
                });
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while deleting the volume {VolumeId}", volumeId);
                throw;
            }
        }

        public long GetVolumeUsedBytes(string volumeId)
        {
            var path = GetVolumeDirectory(volumeId);
            if (!Directory.Exists(path))
            {
                return 0;
            }

            try
            {
                var dirInfo = new DirectoryInfo(path);
                return dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fully sum the used storage space for {VolumeId}.", volumeId);
                return 0;
            }
        }

        public long GetAvailableStorageBytes()
        {
            if (OperatingSystem.IsLinux())
            {
                return FileSystem.GetLinuxAvailableBytes(_storageRoot, _logger);
            }

            var drive = new DriveInfo(Path.GetPathRoot(_storageRoot) ?? _storageRoot);
            return drive.AvailableFreeSpace;
        }

        private static void ValidateVolumeId(string volumeId)
        {
            if (string.IsNullOrWhiteSpace(volumeId) || !SafeVolumeIdRegex().IsMatch(volumeId))
            {
                throw new ArgumentException($"Invalid Volume-ID: '{volumeId}'. Only alphanumeric characters as well as '.', '-', and '_' are allowed.");
            }
        }

    }
}