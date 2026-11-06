using DryDock.Configuration;
using DryDock.Contract;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;
using NCrontab;

namespace DryDock.Services.Background
{
    public class BackupWorker : BackgroundService
    {
        private readonly IStorageManager _storage;
        private readonly IBackupClient _backupClient;
        private readonly DryDockOptions _options;
        private readonly ILogger _logger;
        private readonly CrontabSchedule _schedule;
        private readonly IKubernetes _k8sClient;
        private readonly IVolumeLocker _volumeLocker;

        public BackupWorker(
            IBackupClient backupClient,
            IStorageManager storage,
            ILogger<BackupWorker> logger,
            IOptions<DryDockOptions> options,
            IKubernetes k8sClient,
            IVolumeLocker volumeLocker)
        {
            _backupClient = backupClient;
            _storage = storage;
            _logger = logger;
            _options = options.Value;
            _schedule = CrontabSchedule.Parse(_options.BackupCronExpression);
            _k8sClient = k8sClient;
            _volumeLocker = volumeLocker;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Backup background worker initialized. Cron schedule: '{Cron}'", _options.BackupCronExpression);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var nextOccurrence = _schedule.GetNextOccurrence(now);
                var delay = nextOccurrence - now;

                if (delay <= TimeSpan.Zero)
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                _logger.LogInformation("Next scheduled backup run at: {NextTime} UTC (in {Hours:F1}h)",
                    nextOccurrence, delay.TotalHours);

                await Task.Delay(delay, stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    await RunNodeBackupAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backup execution failed.");
                }
            }
        }

        private async Task RunNodeBackupAsync(CancellationToken ct)
        {

            _logger.LogInformation("Starting scheduled backup cycle for node '{Node}'...", _options.NodeName);

            string rootDir = _options.LocalStorageRoot;
            if (!Directory.Exists(rootDir))
            {
                _logger.LogWarning("Local storage root directory '{RootDir}' does not exist. Skipping backup cycle.", rootDir);
                return;
            }

            var volumeDirectories = Directory.EnumerateDirectories(rootDir)
                .Where(dir => !Path.GetFileName(dir).StartsWith('.'))
                .ToList();

            var pvList = await _k8sClient.CoreV1.ListPersistentVolumeAsync(cancellationToken: ct);

            int backedUpCount = 0;

            foreach (var volumeDir in volumeDirectories)
            {
                if (ct.IsCancellationRequested) break;

                string volumeId = Path.GetFileName(volumeDir);

                var pv = pvList.Items.FirstOrDefault(p => p.Metadata?.Name == volumeId);
                if (pv == null)
                {
                    _logger.LogWarning("PersistentVolume '{VolumeId}' not found in Kubernetes. Skipping backup.", volumeId);
                    continue;
                }

                if (!IsBackupEnabled(pv))
                {
                    _logger.LogDebug("Skipping volume {VolumeId}: backup is disabled via annotation.", volumeId);
                    continue;
                }

                if (pv.Metadata?.Annotations == null ||
                    !pv.Metadata.Annotations.TryGetValue(DryDockOptions.OwnerAnnotationKey, out var owner) ||
                    !string.Equals(owner, _options.NodeName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping volume {VolumeId}: not owned by node '{Node}'.", volumeId, _options.NodeName);
                    continue;
                }

                if (!_storage.ExistsAndHasData(volumeId))
                {
                    _logger.LogDebug("Skipping volume {VolumeId}: empty directory.", volumeId);
                    continue;
                }

                try
                {
                    using (var lockHandle = await _volumeLocker.LockVolumeAsync(volumeId, ct))
                    {
                        await _backupClient.BackupVolumeAsync(volumeId, volumeDir, ct);
                        backedUpCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backup execution failed for volume {VolumeId}.", volumeId);
                }
            }

            _logger.LogInformation("Completed backup cycle for node '{Node}'. Total volumes processed: {Count}",
                _options.NodeName, backedUpCount);
        }

        public bool IsBackupEnabled(V1PersistentVolume pv)
        {
            if (pv.Spec?.Csi?.VolumeAttributes != null &&
                pv.Spec.Csi.VolumeAttributes.TryGetValue(DryDockOptions.BackupAnnotationKey, out var csiVal))
            {
                return !string.Equals(csiVal, "false", StringComparison.OrdinalIgnoreCase);
            }

            if (pv.Metadata?.Annotations != null &&
                pv.Metadata.Annotations.TryGetValue(DryDockOptions.BackupAnnotationKey, out var annotationVal))
            {
                return !string.Equals(annotationVal, "false", StringComparison.OrdinalIgnoreCase);
            }

            return _options.DefaultBackupEnabled;
        }
    }
}