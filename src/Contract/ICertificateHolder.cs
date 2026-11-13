using System.Security.Cryptography.X509Certificates;

namespace DryDock.Contract
{
    public interface ICertificateHolder : IDisposable
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        X509Certificate2 GetCertificate();
        bool ValidateRemoteCertificate(X509Certificate? certificate);
        X509Certificate2 GetCaCertificate();
        void SwapCertificates(X509Certificate2 newCert, X509Certificate2 newCaCert);
    }
}