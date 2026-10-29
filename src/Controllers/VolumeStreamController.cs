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

        public VolumeStreamController(IStorageManager storage)
        {
            _storage = storage;
        }

        [HttpGet("{volumeId}/stream")]
        public async Task GetVolumeTarZstStream(string volumeId, CancellationToken ct)
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