using DryDock.Configuration;
using DryDock.Contract;
using DryDock.Util;
using k8s;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Background
{
    public class CleanupWorker : BackgroundService
    {
        private readonly IStorageManager _storage;
        private readonly DryDockOptions _options;
        private readonly ILogger _logger;
        private readonly IKubernetes _k8sClient;
        private readonly IVolumeLocker _volumeLocker;
        private readonly TimeSpan _directoryAgeThreshold;

        public CleanupWorker(
            IStorageManager storage,
            ILogger<CleanupWorker> logger,
            IOptions<DryDockOptions> options,
            IKubernetes k8sClient,
            IVolumeLocker volumeLocker)
        {
            _storage = storage;
            _logger = logger;
            _options = options.Value;
            _k8sClient = k8sClient;
            _volumeLocker = volumeLocker;
            _directoryAgeThreshold = TimeSpan.FromSeconds(_options.VolumeCleanupThresholdSeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Cleanup background worker initialized. Cron schedule: '{Cron}'", _options.CleanupCronExpression);

            var schedule = NCrontab.CrontabSchedule.Parse(_options.CleanupCronExpression);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var nextOccurrence = schedule.GetNextOccurrence(now);
                var delay = nextOccurrence - now;

                if (delay <= TimeSpan.Zero)
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                _logger.LogInformation("Next scheduled cleanup run at: {NextTime} UTC (in {Hours:F1}h)",
                    nextOccurrence, delay.TotalHours);

                await Task.Delay(delay, stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    await RunCleanupAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred during the cleanup process.");
                }
            }
        }

        private async Task RunCleanupAsync(CancellationToken ct)
        {
            _logger.LogInformation("Starting scheduled cleanup cycle for node '{Node}'...", _options.NodeName);

            string rootDir = _options.LocalStorageRoot;
            if (!Directory.Exists(rootDir))
            {
                _logger.LogWarning("Local storage root directory '{RootDir}' does not exist. Skipping cleanup cycle.", rootDir);
                return;
            }

            var volumeDirectories = Directory.EnumerateDirectories(rootDir);
            var volumeIds = volumeDirectories
                .Select(GetVolumeIdFromDirectoryName)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            if (volumeIds.Count == 0)
            {
                _logger.LogInformation("No valid volume directories found. Skipping cleanup cycle.");
                return;
            }

            var activeVolumeIds = await _k8sClient.CoreV1.ListPersistentVolumeAsync(cancellationToken: ct);
            var localVolumeIds = activeVolumeIds.Items
                .Where(pv => pv.Metadata?.Annotations != null &&
                             pv.Metadata.Annotations.TryGetValue(DryDockOptions.OwnerAnnotationKey, out var owner) &&
                             string.Equals(owner, _options.NodeName, StringComparison.OrdinalIgnoreCase))
                .Select(pv => pv.Metadata.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var volumeId in volumeIds)
            {
                if (localVolumeIds.Contains(volumeId))
                {
                    _logger.LogInformation("Volume '{VolumeId}' is still active. Skipping cleanup.", volumeId);
                    continue;
                }

                var volumeLastWriteTime = Directory.GetLastWriteTimeUtc(Path.Combine(rootDir, volumeId));
                if (DateTime.UtcNow - volumeLastWriteTime < _directoryAgeThreshold)
                {
                    _logger.LogInformation("Volume '{VolumeId}' was created less than {Threshold} ago. Skipping cleanup.", volumeId, _directoryAgeThreshold);
                    continue;
                }

                if (_volumeLocker.IsVolumeLocked(volumeId))
                {
                    _logger.LogInformation("Volume '{VolumeId}' is currently locked. Skipping cleanup.", volumeId);
                    continue;
                }

                using (await _volumeLocker.LockVolumeAsync(volumeId, ct))
                {
                    var dirPath = Path.Combine(rootDir, volumeId);
                    if (LinuxMountDetector.IsPathMounted(dirPath, volumeId))
                    {
                        _logger.LogInformation("Volume '{VolumeId}' is still mounted. Skipping cleanup.", volumeId);
                        continue;
                    }
                    _logger.LogInformation("Volume '{VolumeId}' is no longer active on this node. Proceeding with cleanup.", volumeId);
                    _storage.DeleteVolumeDirectory(volumeId);
                }
            }
        }

        private string GetVolumeIdFromDirectoryName(string directoryName)
        {
            return Path.GetFileName(directoryName);
        }
    }
}