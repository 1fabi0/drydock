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

                await RunCleanupAsync(stoppingToken);
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
            var volumeIds = volumeDirectories.Select(GetVolumeIdFromDirectoryName).ToList();
            var activeVolumeIds = await _k8sClient.CoreV1.ListPersistentVolumeAsync(cancellationToken: ct, labelSelector: DryDockOptions.OwnerAnnotationKey + "=" + _options.NodeName);
            foreach (var volumeId in volumeIds)
            {

                if (activeVolumeIds.Items.Any(pv => string.Equals(pv.Metadata?.Name, volumeId)))
                {
                    _logger.LogInformation("Volume '{VolumeId}' is still active. Skipping cleanup.", volumeId);
                    continue;
                }
                else if(_volumeLocker.IsVolumeLocked(volumeId))
                {
                    _logger.LogInformation("Volume '{VolumeId}' is currently locked. Skipping cleanup.", volumeId);
                    continue;
                }
                else if(LinuxMountDetector.IsPathMounted(Path.Combine(rootDir, volumeId), volumeId))
                {
                    _logger.LogInformation("Volume '{VolumeId}' is currently mounted. Skipping cleanup.", volumeId);
                    continue;
                }
                else
                {
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