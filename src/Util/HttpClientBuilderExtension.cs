using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DryDock.Configuration;
using Microsoft.Extensions.Options;

namespace DryDock.Util
{
    public static class HttpClientBuilderExtension
    {
        public static IHttpClientBuilder ConfigureCACertificate(this IHttpClientBuilder builder)
        {
            return builder.ConfigurePrimaryHttpMessageHandler(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<DryDockOptions>>().Value;
                    var logger = sp.GetRequiredService<ILogger<Program>>();

                    var handler = new SocketsHttpHandler();

                    DateTime lastCaReadTime = DateTime.MinValue;
                    X509Certificate2? cachedCaCert = null;
                    var syncLock = new object();

                    handler.SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                        {
                            if (certificate == null) return false;

                            if (File.Exists(options.CaCertPath))
                            {
                                var fileInfo = new FileInfo(options.CaCertPath);
                                if (fileInfo.LastWriteTimeUtc > lastCaReadTime || cachedCaCert == null)
                                {
                                    lock (syncLock)
                                    {
                                        if (fileInfo.LastWriteTimeUtc > lastCaReadTime || cachedCaCert == null)
                                        {
                                            try
                                            {
                                                cachedCaCert?.Dispose();
                                                cachedCaCert = X509Certificate2.CreateFromPemFile(options.CaCertPath);
                                                lastCaReadTime = fileInfo.LastWriteTimeUtc;
                                                logger.LogInformation("Cluster-CA reloaded from {Path}", options.CaCertPath);
                                            }
                                            catch (Exception ex)
                                            {
                                                logger.LogError(ex, "Error reloading CA from {Path}", options.CaCertPath);
                                            }
                                        }
                                    }
                                }
                            }

                            if (cachedCaCert != null)
                            {
                                using var customChain = new X509Chain();
                                customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                                customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                                customChain.ChainPolicy.CustomTrustStore.Add(cachedCaCert);

                                using var serverCert = new X509Certificate2(certificate);
                                bool isChainValid = customChain.Build(serverCert);

                                if (!isChainValid)
                                {
                                    logger.LogWarning("Certificate chain validation failed against the cluster CA.");
                                    return false;
                                }
                            }

                            return (sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
                        }
                    };

                    return handler;
                });
        }
    }
}