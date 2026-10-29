using Csi.V1;
using DryDock.Configuration;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Csi
{
    public class CsiIdentityService : Identity.IdentityBase
    {
        public const string PluginName = "drydock.io/csi";
        private readonly string _pluginVersion;

        public CsiIdentityService(IOptions<DryDockOptions> options)
        {
            _pluginVersion = options.Value.PluginVersion;
        }

        public override Task<GetPluginInfoResponse> GetPluginInfo(
            GetPluginInfoRequest request, ServerCallContext context)
        {
            return Task.FromResult(new GetPluginInfoResponse
            {
                Name = PluginName,
                VendorVersion = _pluginVersion
            });
        }

        public override Task<GetPluginCapabilitiesResponse> GetPluginCapabilities(
            GetPluginCapabilitiesRequest request, ServerCallContext context)
        {
            var response = new GetPluginCapabilitiesResponse();

            response.Capabilities.Add(new PluginCapability
            {
                Service = new PluginCapability.Types.Service
                {
                    Type = PluginCapability.Types.Service.Types.Type.ControllerService
                }
            });

            return Task.FromResult(response);
        }

        public override Task<ProbeResponse> Probe(ProbeRequest request, ServerCallContext context)
        {
            return Task.FromResult(new ProbeResponse { Ready = true });
        }
    }
}