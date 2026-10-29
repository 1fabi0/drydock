namespace DryDock.Contract
{
    public interface IStorageManager
    {
        string GetVolumeDirectory(string volumeId);
        bool ExistsAndHasData(string volumeId);
        void EnsureVolumeDirectoryCreated(string volumeId);
        void DeleteVolumeDirectory(string volumeId);
        long GetVolumeUsedBytes(string volumeId);
        long GetAvailableStorageBytes();
    }
}