using System.Security.Cryptography.X509Certificates;

namespace DryDock.Contract
{
    public interface ICertificateHolder : IDisposable
    {
        X509Certificate2 GetCertificate();
        bool ValidateRemoteCertificate(X509Certificate? certificate);
        X509Certificate2? GetCaCertificate();
    }
}