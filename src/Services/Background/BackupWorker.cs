using DryDock.Configuration;
using DryDock.Contract;
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

        public BackupWorker(IBackupClient backupClient, IStorageManager storage, ILogger<BackupWorker> logger, IOptions<DryDockOptions> options)
        {
            _backupClient = backupClient;
            _storage = storage;
            _logger = logger;
            _options = options.Value;
            _schedule = CrontabSchedule.Parse(_options.BackupCronExpression);
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

                await RunNodeBackupAsync(stoppingToken);
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

            int backedUpCount = 0;

            foreach (var volumeDir in volumeDirectories)
            {
                if (ct.IsCancellationRequested) break;

                string volumeId = Path.GetFileName(volumeDir);

                if (!_storage.ExistsAndHasData(volumeId))
                {
                    _logger.LogDebug("Skipping volume {VolumeId}: empty directory.", volumeId);
                    continue;
                }

                try
                {
                    await _backupClient.BackupVolumeAsync(volumeId, volumeDir, ct);
                    backedUpCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backup execution failed for volume {VolumeId}.", volumeId);
                }
            }

            _logger.LogInformation("Completed backup cycle for node '{Node}'. Total volumes processed: {Count}",
                _options.NodeName, backedUpCount);
        }
    }
}