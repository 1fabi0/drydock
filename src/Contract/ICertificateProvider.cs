using System.Security.Cryptography.X509Certificates;

namespace DryDock.Contract
{
    public interface ICertificateProvider
    {
        Task<(X509Certificate2 cert, X509Certificate2 caCert)> RequestNewCertificateAsync(CancellationToken cancellationToken);
    }
    
}