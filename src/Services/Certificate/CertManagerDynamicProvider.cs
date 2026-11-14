using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DryDock.Configuration;
using DryDock.Contract;
using DryDock.Util.Security;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Certificate
{
    public class CertManagerDynamicProvider : ICertificateProvider
    {
        private readonly ILogger<CertManagerDynamicProvider> _logger;
        private readonly DryDockOptions _options;
        private readonly IKubernetes _k8sClient;

        public CertManagerDynamicProvider(
            ILogger<CertManagerDynamicProvider> logger,
            IOptions<DryDockOptions> options,
            IKubernetes k8sClient)
        {
            _logger = logger;
            _options = options.Value;
            _k8sClient = k8sClient;
        }

        public async Task<(X509Certificate2 cert, X509Certificate2 caCert)> RequestNewCertificateAsync(CancellationToken cancellationToken)
        {
            string commonName = $"drydock-node-{_options.NodeName}";
            var dnsNames = new List<string>
            {
                _options.NodeName,
                $"{_options.NodeName}.drydock-nodes",
                "localhost"
            };

            var ipAddresses = new List<IPAddress>();
            if (!string.IsNullOrWhiteSpace(_options.NodeIp) && IPAddress.TryParse(_options.NodeIp, out var ip))
            {
                ipAddresses.Add(ip);
            }

            var (csrPem, rawPrivateKey) = CsrGenerator.CreateEcdsaCsr(commonName, dnsNames, ipAddresses);
            using var privateKey = rawPrivateKey;

            string crName = $"drydock-csr-{_options.NodeName.ToLowerInvariant()}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string namespaceName = _options.Namespace;

            var certRequest = new V1CertificateRequest
            {
                Metadata = new V1ObjectMeta
                {
                    Name = crName,
                    NamespaceProperty = namespaceName
                },
                Usages = new List<string>
                {
                    "digital signature",
                    "key encipherment",
                    "server auth",
                    "client auth"
                },
                Spec = new CertificateRequestSpec
                {
                    Request = Convert.ToBase64String(Encoding.UTF8.GetBytes(csrPem)),
                    Duration = _options.CertificateDuration,
                    IssuerRef = new ObjectReference
                    {
                        Name = _options.CertManagerIssuerName,
                        Kind = _options.CertManagerIssuerKind
                    }
                }
            };

            _logger.LogInformation("Submitting CertificateRequest '{Name}' to cert-manager in namespace '{Namespace}'...", crName, namespaceName);
            var created = false;
            try
            {
                await _k8sClient.CustomObjects.CreateNamespacedCustomObjectAsync(
                    certRequest,
                    group: "cert-manager.io",
                    version: "v1",
                    namespaceParameter: namespaceName,
                    plural: "certificaterequests",
                    cancellationToken: cancellationToken);

                created = true;

                var status = await WaitForSigningAsync(crName, namespaceName, cancellationToken);

                X509Certificate2? caCert = null;
                if (string.IsNullOrEmpty(status.CA))
                {
                    throw new InvalidOperationException("CA certificate is missing in the CertificateRequest status.");
                }

                byte[] caBytes = Convert.FromBase64String(status.CA);
                caCert = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(caBytes));

                try
                {
                    byte[] certBytes = Convert.FromBase64String(status.Certificate!);
                    using var publicCert = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(certBytes));

                    var certWithKey = publicCert.CopyWithPrivateKey(privateKey);

                    _logger.LogInformation("Successfully received signed certificate for '{CommonName}'. Valid until: {NotAfter} UTC",
                        commonName, certWithKey.NotAfter);

                    return (certWithKey, caCert);
                }
                catch
                {
                    caCert.Dispose();
                    throw;
                }
            }
            finally
            {
                try
                {
                    if(created)
                    {
                        await _k8sClient.CustomObjects.DeleteNamespacedCustomObjectAsync(
                            group: "cert-manager.io",
                            version: "v1",
                            namespaceParameter: namespaceName,
                            plural: "certificaterequests",
                            name: crName,
                            cancellationToken: CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up CertificateRequest '{Name}' in K8s.", crName);
                }
            }
        }

        private async Task<CertificateRequestStatus> WaitForSigningAsync(string name, string ns, CancellationToken ct)
        {
            var timeout = TimeSpan.FromSeconds(60);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), linkedCts.Token);

                var rawObj = await _k8sClient.CustomObjects.GetNamespacedCustomObjectAsync(
                    group: "cert-manager.io",
                    version: "v1",
                    namespaceParameter: ns,
                    plural: "certificaterequests",
                    name: name,
                    cancellationToken: linkedCts.Token);

                var json = JsonSerializer.Serialize(rawObj);
                var cr = JsonSerializer.Deserialize<V1CertificateRequest>(json);

                if (cr?.Status != null)
                {
                    var readyCondition = cr.Status.Conditions?.FirstOrDefault(c => c.Type == "Ready");
                    if (readyCondition != null)
                    {
                        if (string.Equals(readyCondition.Status, "True", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(cr.Status.Certificate))
                        {
                            return cr.Status;
                        }

                        if (string.Equals(readyCondition.Status, "False", StringComparison.OrdinalIgnoreCase) &&
                            (readyCondition.Reason == "Failed" || readyCondition.Reason == "Denied"))
                        {
                            throw new InvalidOperationException($"CertificateRequest failed: {readyCondition.Message}");
                        }
                    }
                }
            }

            throw new TimeoutException($"Timed out waiting for CertificateRequest '{name}' to be signed by cert-manager.");
        }
    }
}
