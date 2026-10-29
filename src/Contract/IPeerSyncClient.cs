namespace DryDock.Contract
{
    public interface IPeerSyncClient
    {
        Task<bool> TryPullVolumeAsync(string sourceNode, string volumeId, string localSourcePath, CancellationToken cancellationToken);
    }
}