using Csi.V1;
using Drydock.Interop;
using DryDock.Configuration;
using DryDock.Contract;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Csi
{
    public class CsiNodeService : Node.NodeBase
    {
        private readonly IStorageManager _storage;
        private readonly IPeerSyncClient _peerSync;
        private readonly IBackupClient _backup;
        private readonly DryDockOptions _options;
        private readonly ILogger _logger;

        public CsiNodeService(
        IStorageManager storage,
        IPeerSyncClient peerSync,
        IBackupClient backup,
        IOptions<DryDockOptions> options,
        ILogger<CsiNodeService> logger)
        {
            _storage = storage;
            _peerSync = peerSync;
            _backup = backup;
            _logger = logger;
            _options = options.Value;
        }

        public override async Task<NodePublishVolumeResponse> NodePublishVolume(
            NodePublishVolumeRequest request, ServerCallContext context)
        {
            string volumeId = request.VolumeId;
            string targetPath = request.TargetPath;
            string localSourcePath = _storage.GetVolumeDirectory(volumeId);

            if (!_storage.ExistsAndHasData(volumeId))
            {
                _logger.LogInformation("Volume {VolumeId} not found locally. Starting sync...", volumeId);

                request.VolumeContext.TryGetValue("sourceNode", out var sourceNode);

                bool synced = false;
                if (!string.IsNullOrEmpty(sourceNode))
                {
                    synced = await _peerSync.TryPullVolumeAsync(sourceNode, volumeId, localSourcePath, context.CancellationToken);
                }

                if (!synced)
                {
                    _logger.LogWarning("P2P Sync failed for Volume {VolumeId}. Attempting backup restore...", volumeId);
                    await _backup.DownloadVolumeAsync(volumeId, localSourcePath, context.CancellationToken);
                }
            }

            LinuxMount.BindMount(localSourcePath, targetPath);

            return new NodePublishVolumeResponse();
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