using DryDock.Configuration;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Background
{
    public class CapacityReporterWorker : BackgroundService
    {
        private readonly ILogger _logger;
        private readonly IKubernetes _k8s;
        private readonly DryDockOptions _options;

        public CapacityReporterWorker(
            IKubernetes k8s,
            IOptions<DryDockOptions> options,
            ILogger<CapacityReporterWorker> logger)
        {
            _k8s = k8s;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var resourceName = $"drydock-cap-{_options.NodeName.ToLowerInvariant().Trim('-')}";
            const string targetNamespace = "kube-system";

            long? lastReportedBytes = null;
            bool existsOnCluster = false;

            // Schwellenwert: Nur melden, wenn sich der Speicher um mehr als z. B. 100 MB geändert hat
            const long changeThresholdBytes = 100 * 1024 * 1024;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var drive = new DriveInfo(_options.LocalStorageRoot);
                    long availableBytes = drive.AvailableFreeSpace;

                    bool hasSignificantChange = lastReportedBytes == null ||
                                               Math.Abs(availableBytes - lastReportedBytes.Value) >= changeThresholdBytes;

                    if (!hasSignificantChange && existsOnCluster)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_options.CapacityReportIntervalSeconds), stoppingToken);
                        continue;
                    }

                    if (!existsOnCluster)
                    {
                        var capacity = new V1CSIStorageCapacity
                        {
                            ApiVersion = "storage.k8s.io/v1",
                            Kind = "CSIStorageCapacity",
                            Metadata = new V1ObjectMeta
                            {
                                Name = resourceName,
                                NamespaceProperty = targetNamespace
                            },
                            StorageClassName = _options.StorageClassName,
                            Capacity = new ResourceQuantity($"{availableBytes}"),
                            NodeTopology = new V1LabelSelector
                            {
                                MatchLabels = new Dictionary<string, string>
                                {
                                    ["kubernetes.io/hostname"] = _options.NodeName
                                }
                            }
                        };

                        try
                        {
                            await _k8s.StorageV1.CreateNamespacedCSIStorageCapacityAsync(
                                capacity,
                                targetNamespace,
                                cancellationToken: stoppingToken);

                            existsOnCluster = true;
                            lastReportedBytes = availableBytes;
                            _logger.LogInformation("CSIStorageCapacity {Name} initially created in {Namespace} with {Bytes} Bytes.", resourceName, targetNamespace, availableBytes);
                        }
                        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
                        {
                            existsOnCluster = true;
                        }
                    }

                    if (existsOnCluster && hasSignificantChange)
                    {
                        var patchDoc = $$"""
                            {
                                "capacity": "{{availableBytes}}"
                            }
                        """;

                        var patch = new V1Patch(patchDoc, V1Patch.PatchType.MergePatch);

                        await _k8s.StorageV1.PatchNamespacedCSIStorageCapacityAsync(
                            patch,
                            resourceName,
                            targetNamespace,
                            cancellationToken: stoppingToken);

                        lastReportedBytes = availableBytes;
                        _logger.LogDebug("CSIStorageCapacity {Name} in {Namespace} refreshed with {Bytes} Bytes.", resourceName, targetNamespace, availableBytes);
                    }
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    existsOnCluster = false;
                    lastReportedBytes = null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to report CSIStorageCapacity for node {NodeName}.", _options.NodeName);
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.CapacityReportIntervalSeconds), stoppingToken);
            }
        }
    }
}