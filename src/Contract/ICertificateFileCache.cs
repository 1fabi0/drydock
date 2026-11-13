using System.Security.Cryptography.X509Certificates;

namespace DryDock.Contract
{
    public interface ICertificateFileCache
    {
        void WriteCertificateToFile(X509Certificate2 cert);
        void WriteCaCertificateToFile(X509Certificate2 caCert);
        X509Certificate2? ReadCertificateFromFile();
        X509Certificate2? ReadCaCertificateFromFile();
    }
}