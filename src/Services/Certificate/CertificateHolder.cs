using System.Security.Cryptography.X509Certificates;
using DryDock.Contract;

namespace DryDock.Services.Certificate
{
    public class CertificateHolder : ICertificateHolder
    {
        private readonly ILogger _logger;
        private readonly ICertificateFileCache _certFileCache;
        private readonly ICertificateProvider _certProvider;
        private X509Certificate2? _currentCert;
        private X509Certificate2? _currentCaCert;

        public CertificateHolder(
            ILogger<CertificateHolder> logger,
            ICertificateFileCache certFileCache,
            ICertificateProvider certProvider)
        {
            _certFileCache = certFileCache;
            _certProvider = certProvider;
            _logger = logger;
        }

        public X509Certificate2 GetCertificate()
        {
            return _currentCert ?? throw new InvalidOperationException($"Node certificate is not loaded yet. Please call InitializeAsync() first.");
        }

        public X509Certificate2 GetCaCertificate()
        {
            return _currentCaCert ?? throw new InvalidOperationException($"Cluster CA certificate is not loaded yet. Please call InitializeAsync() first.");
        }

        public bool ValidateRemoteCertificate(X509Certificate? certificate)
        {
            if (certificate == null)
            {
                return false;
            }

            var ca = _currentCaCert;
            if (ca == null)
            {
                _logger.LogWarning("Cluster CA is not available, remote certificate validation rejected.");
                return false;
            }

            using var customChain = new X509Chain();
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(ca);

            using var remoteCert2 = new X509Certificate2(certificate);
            bool isValid = customChain.Build(remoteCert2);

            if (!isValid)
            {
                _logger.LogWarning("Remote certificate chain validation failed against Drydock cluster CA.");
            }

            return isValid;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            var cert = _certFileCache.ReadCertificateFromFile();
            var caCert = _certFileCache.ReadCaCertificateFromFile();

            if (cert != null && caCert != null)
            {
                _currentCert = cert;
                _currentCaCert = caCert;
                return;
            }

            (cert, caCert) = await _certProvider.RequestNewCertificateAsync(cancellationToken);
            _certFileCache.WriteCertificateToFile(cert);
            _certFileCache.WriteCaCertificateToFile(caCert);

            _currentCert = cert;
            _currentCaCert = caCert;
        }

        public void SwapCertificates(X509Certificate2 newCert, X509Certificate2 newCaCert)
        {
            _certFileCache.WriteCertificateToFile(newCert);
            _certFileCache.WriteCaCertificateToFile(newCaCert);

            var oldCert = Interlocked.Exchange(ref _currentCert, newCert);
            var oldCaCert = Interlocked.Exchange(ref _currentCaCert, newCaCert);

            oldCert?.Dispose();
            oldCaCert?.Dispose();

        }

        public void Dispose()
        {
            _currentCert?.Dispose();
            _currentCaCert?.Dispose();
        }
    }
}