using DryDock.Configuration;
using DryDock.Contract;
using DryDock.Util;
using Grpc.Core;
using k8s;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Storage
{
    public class PeerSyncClient : IPeerSyncClient
    {
        private readonly HttpClient _httpClient;
        private readonly IKubernetes _k8sClient;
        private readonly ILogger _logger;
        private readonly DryDockOptions _options;

        public PeerSyncClient(HttpClient httpClient, ILogger<PeerSyncClient> logger, IOptions<DryDockOptions> options, IKubernetes k8sClient)
        {
            _httpClient = httpClient;
            _logger = logger;
            _options = options.Value;
            _k8sClient = k8sClient;
        }

        public async Task DeleteVolumeOnAllPeersAsync(string volumeId, CancellationToken cancellationToken)
        {
            var podList = await _k8sClient.CoreV1.ListNamespacedPodAsync(
                namespaceParameter: _options.Namespace,
                labelSelector: _options.MatchLabels,
                cancellationToken: cancellationToken);

            var eligiblePods = podList.Items
                .Where(pod => pod.Status.Phase == "Running" &&
                              pod.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True"))
                .Where(pod => !string.Equals(pod.Spec.NodeName, _options.NodeName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var deleteTasks = eligiblePods.Select(async pod =>
            {
                string targetNodeName = pod.Spec.NodeName;
                string? targetNodeAddress = pod.Status?.PodIP;

                if (string.IsNullOrEmpty(targetNodeAddress))
                {
                    var node = await _k8sClient.CoreV1.ReadNodeAsync(targetNodeName, cancellationToken: cancellationToken);
                    targetNodeAddress = node.Status.Addresses
                        .FirstOrDefault(a => a.Type == "InternalIP")?.Address;
                }

                if (string.IsNullOrEmpty(targetNodeAddress))
                {
                    _logger.LogWarning("Could not resolve IP address for node {NodeName}. Skipping volume deletion on this node.", targetNodeName);
                    return;
                }

                string url = $"https://{targetNodeAddress}:{_options.VolumeSyncingPort}/api/volumes/{volumeId}/delete";

                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                    var response = await _httpClient.DeleteAsync(url, linkedCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to delete volume {VolumeId} on node {NodeName}. HTTP {Status}", volumeId, targetNodeName, response.StatusCode);
                        throw new RpcException(new Status(StatusCode.Internal, $"Failed to delete volume {volumeId} on node {targetNodeName}. HTTP {response.StatusCode}"));
                    }
                    else
                    {
                        _logger.LogInformation("Successfully deleted volume {VolumeId} on node {NodeName}.", volumeId, targetNodeName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while deleting volume {VolumeId} on node {NodeName}.", volumeId, targetNodeName);
                    throw new RpcException(new Status(StatusCode.Internal, $"Error while deleting volume {volumeId} on node {targetNodeName}: {ex.Message}"));
                }
            });

            await Task.WhenAll(deleteTasks);

            if (eligiblePods.Count < podList.Items.Count - 1)
            {
                _logger.LogWarning("Volume {VolumeId} deletion was not attempted on all eligible nodes. Some nodes may not have been reachable or ready.", volumeId);
                throw new RpcException(new Status(StatusCode.Unavailable, $"Volume {volumeId} deletion was not attempted on all eligible nodes. Some nodes may not have been reachable or ready."));
            }
        }

        public async Task<string> ResolveTargetNodeAddressAsync(
            string targetNodeName,
            CancellationToken ct = default)
        {
            var podList = await _k8sClient.CoreV1.ListNamespacedPodAsync(
                namespaceParameter: _options.Namespace,
                labelSelector: _options.MatchLabels,
                fieldSelector: $"spec.nodeName={targetNodeName}",
                cancellationToken: ct);

            var targetPod = podList.Items.FirstOrDefault(p =>
                p.Status.Phase == "Running" &&
                p.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True"));

            if (targetPod?.Status?.PodIP != null)
            {
                return targetPod.Status.PodIP;
            }

            var node = await _k8sClient.CoreV1.ReadNodeAsync(targetNodeName, cancellationToken: ct);
            var nodeIp = node.Status.Addresses
                .FirstOrDefault(a => a.Type == "InternalIP")?.Address;

            return nodeIp ?? throw new InvalidOperationException(
                $"Could not resolve neither Pod-IP nor InternalIP for node '{targetNodeName}'");
        }

        public async Task<bool> TryPullVolumeAsync(string sourceNode, string volumeId, string localSourcePath, CancellationToken cancellationToken)
        {
            string stagingDir = $"{localSourcePath}.staging-{Guid.NewGuid():N}";
            string url = $"https://{await ResolveTargetNodeAddressAsync(sourceNode, cancellationToken)}:{_options.VolumeSyncingPort}/api/volumes/{volumeId}/stream";

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