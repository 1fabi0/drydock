using System.Collections.Concurrent;
using DryDock.Contract;

namespace DryDock.Services.Storage
{
    public class VolumeLocker : IVolumeLocker
    {
        private sealed class RefCountedSemaphore
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public int RefCount = 1;
        }

        private readonly ConcurrentDictionary<string, RefCountedSemaphore> _semaphores = new();

        public async Task<IDisposable> LockVolumeAsync(string volumeId, CancellationToken cancellationToken = default)
        {
            RefCountedSemaphore item;

            lock (_semaphores)
            {
                if (_semaphores.TryGetValue(volumeId, out var existing))
                {
                    existing.RefCount++;
                    item = existing;
                }
                else
                {
                    item = new RefCountedSemaphore();
                    _semaphores[volumeId] = item;
                }
            }

            try
            {
                await item.Semaphore.WaitAsync(cancellationToken);
            }
            catch
            {
                ReleaseReference(volumeId, item, acquired: false);
                throw;
            }

            return new Releaser(this, volumeId, item);
        }

        public bool IsVolumeLocked(string volumeId)
        {
            return _semaphores.TryGetValue(volumeId, out var item) && item.Semaphore.CurrentCount == 0;
        }

        private void ReleaseReference(string volumeId, RefCountedSemaphore item, bool acquired)
        {
            lock (_semaphores)
            {
                if (acquired)
                {
                    item.Semaphore.Release();
                }

                item.RefCount--;

                if (item.RefCount == 0)
                {
                    _semaphores.TryRemove(volumeId, out _);
                    item.Semaphore.Dispose();
                }
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly VolumeLocker _locker;
            private readonly string _volumeId;
            private readonly RefCountedSemaphore _item;
            private int _disposed;

            public Releaser(VolumeLocker locker, string volumeId, RefCountedSemaphore item)
            {
                _locker = locker;
                _volumeId = volumeId;
                _item = item;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _locker.ReleaseReference(_volumeId, _item, acquired: true);
                }
            }
        }
    }
}