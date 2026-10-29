using Csi.V1;
using DryDock.Configuration;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Csi
{
    public class CsiIdentityService : Identity.IdentityBase
    {
        private readonly string _pluginName;
        private readonly string _pluginVersion;

        public CsiIdentityService(IOptions<DryDockOptions> options)
        {
            _pluginVersion = options.Value.PluginVersion;
            _pluginName = options.Value.PluginName;
        }

        public override Task<GetPluginInfoResponse> GetPluginInfo(
            GetPluginInfoRequest request, ServerCallContext context)
        {
            return Task.FromResult(new GetPluginInfoResponse
            {
                Name = _pluginName,
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