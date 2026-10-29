using Csi.V1;
using DryDock.Configuration;
using DryDock.Contract;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Csi
{
    public class CsiControllerService : Controller.ControllerBase
    {
        private readonly IStorageManager _storage;
        private readonly DryDockOptions _options;
        private readonly ILogger _logger;
        public CsiControllerService(
        IStorageManager storage,
        IOptions<DryDockOptions> options,
        ILogger<CsiControllerService> logger)
        {
            _storage = storage;
            _options = options.Value;
            _logger = logger;
        }

        public override Task<CreateVolumeResponse> CreateVolume(
            CreateVolumeRequest request, ServerCallContext context)
        {
            _logger.LogInformation("CSI CreateVolume called: {VolumeName}", request.Name);

            string volumeId = $"drydock-{request.Name}";

            string? targetNode = null;
            if (request.AccessibilityRequirements != null)
            {
                foreach (var preference in request.AccessibilityRequirements.Preferred)
                {
                    if (preference.Segments.TryGetValue("kubernetes.io/hostname", out var node))
                    {
                        targetNode = node;
                        break;
                    }
                }
            }

            targetNode ??= _options.NodeName;

            var response = new CreateVolumeResponse
            {
                Volume = new Volume
                {
                    VolumeId = volumeId,
                    CapacityBytes = request.CapacityRange?.RequiredBytes ?? 1024L * 1024 * 1024,
                    VolumeContext =
                {
                    ["sourceNode"] = targetNode
                },
                    AccessibleTopology =
                {
                    new Topology
                    {
                        Segments = { ["kubernetes.io/hostname"] = targetNode }
                    }
                }
                }
            };

            return Task.FromResult(response);
        }

        public override Task<DeleteVolumeResponse> DeleteVolume(
            DeleteVolumeRequest request, ServerCallContext context)
        {
            _logger.LogInformation("CSI DeleteVolume called: {VolumeId}", request.VolumeId);

            _storage.DeleteVolumeDirectory(request.VolumeId);

            return Task.FromResult(new DeleteVolumeResponse());
        }

        public override Task<ControllerGetCapabilitiesResponse> ControllerGetCapabilities(
            ControllerGetCapabilitiesRequest request, ServerCallContext context)
        {
            var response = new ControllerGetCapabilitiesResponse();

            response.Capabilities.Add(new ControllerServiceCapability
            {
                Rpc = new ControllerServiceCapability.Types.RPC
                {
                    Type = ControllerServiceCapability.Types.RPC.Types.Type.CreateDeleteVolume
                }
            });

            return Task.FromResult(response);
        }
    }
}