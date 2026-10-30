using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DryDock.Contract;

namespace DryDock.Util
{
    public static class HttpClientBuilderExtension
    {
        public static IHttpClientBuilder ConfigureCACertificate(this IHttpClientBuilder builder)
        {
            return builder.ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var certHolder = sp.GetRequiredService<ICertificateHolder>();

                return new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        LocalCertificateSelectionCallback = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                        {
                            return certHolder.GetCertificate();
                        },

                        RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                        {
                            if (certificate == null) return false;

                            if (!certHolder.ValidateRemoteCertificate(certificate))
                            {
                                return false;
                            }

                            return (sslPolicyErrors & ~(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)) == SslPolicyErrors.None;
                        }
                    }
                };
            });
        }
    }
}