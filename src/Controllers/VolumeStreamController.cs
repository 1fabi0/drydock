using DryDock.Contract;
using DryDock.Util;
using Microsoft.AspNetCore.Mvc;
namespace DryDock.Controllers
{
    [ApiController]
    [Route("api/volumes")]
    public class VolumeStreamController : ControllerBase
    {
        private readonly IStorageManager _storage;
        private readonly IVolumeLocker _volumeLocker;

        public VolumeStreamController(IStorageManager storage, IVolumeLocker volumeLocker)
        {
            _storage = storage;
            _volumeLocker = volumeLocker;
        }

        [HttpGet("{volumeId}/stream")]
        public async Task GetVolumeTarZstStream(string volumeId, CancellationToken ct)
        {
            using (await _volumeLocker.LockVolumeAsync(volumeId, ct))
            {
                string dirPath = _storage.GetVolumeDirectory(volumeId);
                if (!Directory.Exists(dirPath))
                {
                    Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                Response.ContentType = "application/x-zstd";

                await TarZstdCompressor.CompressDirectoryToStreamAsync(dirPath, Response.Body, ct);
            }
        }
    }
}