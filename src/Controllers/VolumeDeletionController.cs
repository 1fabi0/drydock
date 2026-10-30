using DryDock.Contract;
using Microsoft.AspNetCore.Mvc;

namespace DryDock.Controllers
{
    [Route("api/volumes")]
    public class VolumeDeletionController : ControllerBase
    {
        private readonly IStorageManager _storageManager;
        private readonly ILogger<VolumeDeletionController> _logger;
        
        public VolumeDeletionController(IStorageManager storageManager, ILogger<VolumeDeletionController> logger)
        {
            _storageManager = storageManager;
            _logger = logger;
        }

        [HttpDelete("{volumeId}/delete")]
        public IActionResult DeleteVolume(string volumeId)
        {
            try
            {
                _logger.LogInformation("Deleting volume {VolumeId} if it exists.", volumeId);
                _storageManager.DeleteVolumeDirectory(volumeId);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while deleting volume {VolumeId}.", volumeId);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}