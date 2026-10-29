using DryDock.Configuration;
using DryDock.Contract;
using DryDock.Util;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Storage
{
    public class PeerSyncClient : IPeerSyncClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly DryDockOptions _options;

        public PeerSyncClient(HttpClient httpClient, ILogger<PeerSyncClient> logger, IOptions<DryDockOptions> options)
        {
            _httpClient = httpClient;
            _logger = logger;
            _options = options.Value;
        }

        public async Task<bool> TryPullVolumeAsync(string sourceNode, string volumeId, string localSourcePath, CancellationToken cancellationToken)
        {
            string stagingDir = $"{localSourcePath}.staging-{Guid.NewGuid():N}";
            string url = $"https://{sourceNode}:{_options.VolumeSyncingPort}/api/volumes/{volumeId}/stream";

            try
            {
                _logger.LogInformation("Starting HTTP/3 Direct Pull from {SourceNode} for Volume {VolumeId}", sourceNode, volumeId);

                using var response = await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Peer Pull failed. HTTP {Status} from {Url}", response.StatusCode, url);
                    return false;
                }

                await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                await TarZstdExtractor.ExtractTarZstStreamAsync(networkStream, stagingDir, cancellationToken);

                if (Directory.Exists(localSourcePath))
                {
                    Directory.Delete(localSourcePath, recursive: true);
                }
                Directory.Move(stagingDir, localSourcePath);

                _logger.LogInformation("Volume {VolumeId} successfully synchronized via HTTP/3 from {SourceNode}", volumeId, sourceNode);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HTTP/3 Sync from Node {SourceNode} for Volume {VolumeId} failed.", sourceNode, volumeId);
                return false;
            }
            finally
            {
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, recursive: true); } catch { /* best effort */ }
                }
            }
        }
    }
}