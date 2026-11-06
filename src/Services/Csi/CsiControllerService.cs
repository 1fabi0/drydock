using Csi.V1;
using DryDock.Configuration;
using DryDock.Contract;
using Grpc.Core;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Csi
{
    public class CsiControllerService : Controller.ControllerBase
    {
        private readonly IStorageManager _storage;
        private readonly ILogger _logger;
        private readonly IBackupClient _backupClient;
        private readonly DryDockOptions _options;
        private readonly IKubernetes _k8sClient;
        private readonly IVolumeLocker _volumeLocker;
        public CsiControllerService(
            IStorageManager storage,
            IBackupClient backupClient,
            ILogger<CsiControllerService> logger,
            IOptions<DryDockOptions> options,
            IVolumeLocker volumeLocker,
            IKubernetes k8sClient)
        {
            _storage = storage;
            _logger = logger;
            _backupClient = backupClient;
            _options = options.Value;
            _k8sClient = k8sClient;
            _volumeLocker = volumeLocker;
        }

        public override async Task<CreateVolumeResponse> CreateVolume(
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

            bool backupEnabled = _options.DefaultBackupEnabled;

            if (request.Parameters != null)
            {
                if (request.Parameters.TryGetValue("backupEnabled", out var scVal))
                {
                    backupEnabled = !string.Equals(scVal, "false", StringComparison.OrdinalIgnoreCase);
                }

                if (request.Parameters.TryGetValue("csi.storage.k8s.io/pvc/name", out var pvcName) &&
                    request.Parameters.TryGetValue("csi.storage.k8s.io/pvc/namespace", out var pvcNamespace))
                {
                    var pvc = await _k8sClient.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
                        pvcName, pvcNamespace, cancellationToken: context.CancellationToken);

                    if (pvc.Metadata?.Annotations != null &&
                        pvc.Metadata.Annotations.TryGetValue(DryDockOptions.BackupAnnotationKey, out var val))
                    {
                        backupEnabled = !string.Equals(val, "false", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            volumeContext[DryDockOptions.BackupAnnotationKey] = backupEnabled ? "true" : "false";

            return new CreateVolumeResponse
            {
                Volume = new Volume
                {
                    VolumeId = request.Name,
                    CapacityBytes = requiredBytes,
                    VolumeContext = { volumeContext }
                }
            };
        }

        public override async Task<DeleteVolumeResponse> DeleteVolume(
            DeleteVolumeRequest request, ServerCallContext context)
        {
            _logger.LogInformation("CSI DeleteVolume called: {VolumeId}", request.VolumeId);

            if (string.IsNullOrWhiteSpace(request.VolumeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VolumeId must not be empty."));
            }

            // this is pure luck if it is on this node
            if (_storage.ExistsAndHasData(request.VolumeId))
            {
                using (await _volumeLocker.LockVolumeAsync(request.VolumeId, context.CancellationToken))
                {
                    _storage.DeleteVolumeDirectory(request.VolumeId);
                }
            }

            if (_options.IsBackupEnabled)
            {
                bool backupEnabled = _options.DefaultBackupEnabled;

                try
                {
                    var pv = await _k8sClient.CoreV1.ReadPersistentVolumeAsync(
                        request.VolumeId,
                        cancellationToken: context.CancellationToken);

                    backupEnabled = IsBackupEnabled(pv);
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("PV '{VolumeId}' was already removed from API. Proceeding with default backup setting ({Default}).",
                        request.VolumeId, backupEnabled);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to inspect PV '{VolumeId}' metadata. Falling back to default ({Default}).",
                        request.VolumeId, backupEnabled);
                }

                if (backupEnabled)
                {
                    try
                    {
                        await _backupClient.DeleteVolumeBackupAsync(request.VolumeId, context.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete backup for volume {VolumeId}.", request.VolumeId);
                    }
                }
                else
                {
                    _logger.LogDebug("Skipping remote backup deletion for volume {VolumeId}: backups were disabled.", request.VolumeId);
                }
            }
            return new DeleteVolumeResponse();
        }

        public bool IsBackupEnabled(V1PersistentVolume pv)
        {
            if (pv.Spec?.Csi?.VolumeAttributes != null &&
                pv.Spec.Csi.VolumeAttributes.TryGetValue(DryDockOptions.BackupAnnotationKey, out var csiVal))
            {
                return !string.Equals(csiVal, "false", StringComparison.OrdinalIgnoreCase);
            }

            if (pv.Metadata?.Annotations != null &&
                pv.Metadata.Annotations.TryGetValue(DryDockOptions.BackupAnnotationKey, out var annotationVal))
            {
                return !string.Equals(annotationVal, "false", StringComparison.OrdinalIgnoreCase);
            }

            return _options.DefaultBackupEnabled;
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