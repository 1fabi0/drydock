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
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var drive = new DriveInfo(_options.LocalStorageRoot);
                    long availableBytes = drive.AvailableFreeSpace;

                    var node = await _k8s.CoreV1.ReadNodeAsync(_options.NodeName, cancellationToken: stoppingToken);
                    var topologyLabels = node.Metadata.Labels;

                    var capacity = new V1CSIStorageCapacity
                    {
                        ApiVersion = "storage.k8s.io/v1",
                        Kind = "CSIStorageCapacity",
                        Metadata = new V1ObjectMeta
                        {
                            Name = $"drydock-cap-{_options.NodeName.ToLowerInvariant()}",
                            NamespaceProperty = "kube-system"
                        },
                        StorageClassName = _options.StorageClassName,
                        Capacity = new ResourceQuantity($"{availableBytes}"),
                        NodeTopology = new V1LabelSelector
                        {
                            MatchLabels = topologyLabels
                        }
                    };

                    try
                    {
                        await _k8s.StorageV1.CreateNamespacedCSIStorageCapacityAsync(capacity, "kube-system", cancellationToken: stoppingToken);
                    }
                    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        await _k8s.StorageV1.ReplaceNamespacedCSIStorageCapacityAsync(capacity, capacity.Metadata.Name, "kube-system", cancellationToken: stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fehler beim Melden der CSI Storage Capacity.");
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.CapacityReportIntervalSeconds), stoppingToken);
            }
        }
    }
}