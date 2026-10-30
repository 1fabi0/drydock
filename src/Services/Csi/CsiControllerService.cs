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
        private readonly IPeerSyncClient _peerSync;
        public CsiControllerService(
            IStorageManager storage,
            IOptions<DryDockOptions> options,
            IPeerSyncClient peerSync,
            ILogger<CsiControllerService> logger)
        {
            _storage = storage;
            _options = options.Value;
            _logger = logger;
            _peerSync = peerSync;
        }

        public override Task<CreateVolumeResponse> CreateVolume(
            CreateVolumeRequest request, ServerCallContext context)
        {
            _logger.LogInformation("CSI CreateVolume called: {VolumeName}", request.Name);

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Volume Name must not be empty."));
            }

            if (request.VolumeCapabilities == null || request.VolumeCapabilities.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VolumeCapabilities must be provided."));
            }

            long requiredBytes = request.CapacityRange?.RequiredBytes ?? 1024L * 1024L * 1024L;
            var volumeContext = new Dictionary<string, string>();

            if (request.Parameters != null)
            {
                foreach (var (key, value) in request.Parameters)
                {
                    volumeContext[key] = value;
                }
            }

            var response = new CreateVolumeResponse
            {
                Volume = new Volume
                {
                    VolumeId = request.Name,
                    CapacityBytes = requiredBytes,
                    VolumeContext = { volumeContext }
                }
            };

            return Task.FromResult(response);
        }

        public override async Task<DeleteVolumeResponse> DeleteVolume(
            DeleteVolumeRequest request, ServerCallContext context)
        {
            _logger.LogInformation("CSI DeleteVolume called: {VolumeId}", request.VolumeId);

            if (string.IsNullOrWhiteSpace(request.VolumeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VolumeId must not be empty."));
            }

            _storage.DeleteVolumeDirectory(request.VolumeId);
            await _peerSync.DeleteVolumeOnAllPeersAsync(request.VolumeId, context.CancellationToken);

            return new DeleteVolumeResponse();
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

        public override Task<ValidateVolumeCapabilitiesResponse> ValidateVolumeCapabilities(
            ValidateVolumeCapabilitiesRequest request, ServerCallContext context)
        {
            if (string.IsNullOrWhiteSpace(request.VolumeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VolumeId must not be empty."));
            }

            if (request.VolumeCapabilities == null || request.VolumeCapabilities.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VolumeCapabilities must be provided."));
            }

            bool isSupported = true;

            foreach (var capability in request.VolumeCapabilities)
            {
                var mode = capability.AccessMode?.Mode;
                if (mode != VolumeCapability.Types.AccessMode.Types.Mode.SingleNodeSingleWriter 
                && mode != VolumeCapability.Types.AccessMode.Types.Mode.SingleNodeWriter)
                {
                    isSupported = false;
                    break;
                }

                if (capability.AccessTypeCase != VolumeCapability.AccessTypeOneofCase.Mount)
                {
                    isSupported = false;
                    break;
                }
            }

            var response = new ValidateVolumeCapabilitiesResponse();

            if (isSupported)
            {
                response.Confirmed = new ValidateVolumeCapabilitiesResponse.Types.Confirmed
                {
                    VolumeCapabilities = { request.VolumeCapabilities }
                };
            }
            else
            {
                response.Message = "Drydock only supports Mount volume capability with SingleNodeWriter (ReadWriteOnce) access mode.";
            }

            return Task.FromResult(response);
        }
    }
}