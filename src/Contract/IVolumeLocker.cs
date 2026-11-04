namespace DryDock.Contract
{
    public interface IVolumeLocker
    {
        Task<IDisposable> LockVolumeAsync(string volumeId, CancellationToken cancellationToken = default);
        bool IsVolumeLocked(string volumeId);
    }
}