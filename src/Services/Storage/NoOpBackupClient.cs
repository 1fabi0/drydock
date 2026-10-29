using DryDock.Contract;

namespace DryDock.Services.Storage
{
    public class NoOpBackupClient : IBackupClient
    {
        public Task BackupVolumeAsync(string volumeId, string localSourcePath, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("Backup is not activated. Please configure a backup client in the DryDock configuration.");
        }

        public Task DownloadVolumeAsync(string volumeId, string localSourcePath, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("Backup is not activated. Please configure a backup client in the DryDock configuration.");
        }
    }
}