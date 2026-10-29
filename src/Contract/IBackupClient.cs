namespace DryDock.Contract
{
    public interface IBackupClient
    {
        Task DownloadVolumeAsync(string volumeId, string localSourcePath, CancellationToken cancellationToken);   
        Task BackupVolumeAsync(string volumeId, string localSourcePath, CancellationToken cancellationToken);
    }
}