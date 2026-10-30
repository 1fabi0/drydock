using System.Text.Json;
using Csi.V1;
using Drydock.Interop;
using DryDock.Configuration;
using DryDock.Contract;
using Grpc.Core;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Csi
{
    public class CsiNodeService : Node.NodeBase
    {
        private const string OwnerAnnotationKey = "drydock.io/current-node";
        private readonly IStorageManager _storage;
        private readonly IPeerSyncClient _peerSync;
        private readonly IBackupClient _backup;
        private readonly DryDockOptions _options;
        private readonly IKubernetes _k8sClient;
        private readonly ILogger _logger;

        public CsiNodeService(
        IStorageManager storage,
        IPeerSyncClient peerSync,
        IBackupClient backup,
        IKubernetes k8sClient,
        IOptions<DryDockOptions> options,
        ILogger<CsiNodeService> logger)
        {
            _storage = storage;
            _peerSync = peerSync;
            _backup = backup;
            _logger = logger;
            _options = options.Value;
            _k8sClient = k8sClient;
        }

        public override async Task<NodePublishVolumeResponse> NodePublishVolume(
            NodePublishVolumeRequest request, ServerCallContext context)
        {
            string volumeId = request.VolumeId;
            string targetPath = request.TargetPath;

            if (string.IsNullOrWhiteSpace(volumeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VolumeId must be specified."));
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "TargetPath must be specified."));
            }

            string localSourcePath = _storage.GetVolumeDirectory(volumeId);

            string? lastKnownNode = await GetVolumeOwnerNodeAsync(volumeId, context.CancellationToken);

            if (string.IsNullOrEmpty(lastKnownNode))
            {
                _logger.LogInformation("Volume {VolumeId} is new (no owner annotated). Initializing local directory on {NodeName}.", volumeId, _options.NodeName);

                _storage.EnsureVolumeDirectoryCreated(volumeId);
            }
            else if (string.Equals(lastKnownNode, _options.NodeName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Volume {VolumeId} is already owned by this node {NodeName}.", volumeId, _options.NodeName);
                if (_storage.ExistsAndHasData(volumeId))
                {
                    _logger.LogInformation("Volume {VolumeId} already exists on this node {NodeName}. No action needed.", volumeId, _options.NodeName);
                }
                else
                {
                    _logger.LogWarning("Volume {VolumeId} was owned by this node ({NodeName}), but local data is missing.", volumeId, _options.NodeName);

                    bool restored = false;
                    if (_options.IsBackupEnabled)
                    {
                        restored = await TryRestoreFromBackupAsync(volumeId, localSourcePath, context.CancellationToken);
                    }

                    if (!restored)
                    {
                        throw new RpcException(new Status(StatusCode.Unavailable, $"Failed to find {volumeId} on node '{lastKnownNode}' and backup was not available."));
                    }
                }
            }
            else
            {
                _logger.LogInformation("Volume {VolumeId} last known on remote node {RemoteNode}. Initiating P2P pull to {LocalNode}...", volumeId, lastKnownNode, _options.NodeName);

                if (_storage.ExistsAndHasData(volumeId))
                {
                    _logger.LogWarning("Found stale local data for volume {VolumeId} on node {NodeName}. Deleting before P2P pull.", volumeId, _options.NodeName);
                    _storage.DeleteVolumeDirectory(volumeId);
                }

                bool synced = await _peerSync.TryPullVolumeAsync(lastKnownNode, volumeId, localSourcePath, context.CancellationToken);

                if (!synced && _options.IsBackupEnabled)
                {
                    _logger.LogWarning("P2P Sync failed for {VolumeId}. Falling back to backup restore...", volumeId);
                    synced = await TryRestoreFromBackupAsync(volumeId, localSourcePath, context.CancellationToken);
                }

                if (!synced && !_storage.ExistsAndHasData(volumeId))
                {
                    throw new RpcException(new Status(StatusCode.Unavailable, $"Failed to synchronize volume {volumeId} from peer '{lastKnownNode}' and backup was not available."));
                }
            }

            _storage.EnsureVolumeDirectoryCreated(volumeId);
            Directory.CreateDirectory(targetPath);

            try
            {
                LinuxMount.BindMount(localSourcePath, targetPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bind mount {Source} to {Target}", localSourcePath, targetPath);
                throw new RpcException(new Status(StatusCode.Internal, $"Mount operation failed: {ex.Message}"));
            }

            if (!string.Equals(lastKnownNode, _options.NodeName, StringComparison.OrdinalIgnoreCase))
            {
                await UpdateVolumeOwnerAnnotationAsync(volumeId, _options.NodeName, context.CancellationToken);
            }
            
            return new NodePublishVolumeResponse();
        }

        private async Task<string?> GetVolumeOwnerNodeAsync(string volumeId, CancellationToken cancellationToken)
        {
            try
            {
                var pv = await _k8sClient.CoreV1.ReadPersistentVolumeAsync(volumeId, cancellationToken: cancellationToken);
                if (pv.Metadata?.Annotations != null &&
                    pv.Metadata.Annotations.TryGetValue(OwnerAnnotationKey, out var ownerNode) &&
                    !string.IsNullOrWhiteSpace(ownerNode))
                {
                    return ownerNode;
                }
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("PersistentVolume {VolumeId} not found in K8s API.", volumeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading PersistentVolume {VolumeId} to resolve owner node.", volumeId);
            }

            return null;
        }

        private async Task UpdateVolumeOwnerAnnotationAsync(string volumeId, string currentNode, CancellationToken cancellationToken)
        {
            try
            {
                var patchObject = new
                {
                    metadata = new
                    {
                        annotations = new Dictionary<string, string>
                        {
                            [OwnerAnnotationKey] = currentNode
                        }
                    }
                };

                var patchJson = JsonSerializer.Serialize(patchObject);
                var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);

                await _k8sClient.CoreV1.PatchPersistentVolumeAsync(
                    patch,
                    name: volumeId,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Updated PV {VolumeId} owner annotation to '{NodeName}'.", volumeId, currentNode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to patch owner annotation on PersistentVolume {VolumeId}.", volumeId);
            }
        }

        private async Task<bool> TryRestoreFromBackupAsync(string volumeId, string targetPath, CancellationToken cancellationToken)
        {
            try
            {
                await _backup.DownloadVolumeAsync(volumeId, targetPath, cancellationToken);
                return _storage.ExistsAndHasData(volumeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup restore attempt failed for volume {VolumeId}.", volumeId);
                return false;
            }
        }

        public override Task<NodeUnpublishVolumeResponse> NodeUnpublishVolume(
            NodeUnpublishVolumeRequest request, ServerCallContext context)
        {
            LinuxMount.Unmount(request.TargetPath);
            return Task.FromResult(new NodeUnpublishVolumeResponse());
        }

        public override Task<NodeGetCapabilitiesResponse> NodeGetCapabilities(
            NodeGetCapabilitiesRequest request, ServerCallContext context)
        {
            return Task.FromResult(new NodeGetCapabilitiesResponse());
        }

        public override Task<NodeGetInfoResponse> NodeGetInfo(
            NodeGetInfoRequest request, ServerCallContext context)
        {
            return Task.FromResult(new NodeGetInfoResponse
            {
                NodeId = _options.NodeName,
                AccessibleTopology = new Topology
                {
                    Segments = { ["kubernetes.io/hostname"] = _options.NodeName }
                }
            });
        }
    }
}