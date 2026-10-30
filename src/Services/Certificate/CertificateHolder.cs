using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DryDock.Configuration;
using DryDock.Contract;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Certificate
{
    public class CertificateHolder : ICertificateHolder
    {
        private readonly string _certPath;
        private readonly string _keyPath;
        private readonly string _caCertPath;
        private readonly ILogger _logger;
        private readonly object _lock = new();

        private volatile X509Certificate2? _currentCert;
        private DateTime _lastCertWriteTime = DateTime.MinValue;

        private volatile X509Certificate2? _cachedCaCert;
        private DateTime _lastCaWriteTime = DateTime.MinValue;

        private long _lastCheckTicks = 0;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

        public CertificateHolder(IOptions<DryDockOptions> options, ILogger<CertificateHolder> logger)
        {
            _certPath = options.Value.CertPath;
            _keyPath = options.Value.KeyPath;
            _caCertPath = options.Value.CaCertPath;
            _logger = logger;
        }

        public X509Certificate2 GetCertificate()
        {
            EnsureFreshCertificates();
            return _currentCert ?? throw new InvalidOperationException($"Node certificate could not be loaded from {_certPath}");
        }

        public X509Certificate2? GetCaCertificate()
        {
            EnsureFreshCertificates();
            return _cachedCaCert;
        }

        public bool ValidateRemoteCertificate(X509Certificate? certificate)
        {
            if (certificate == null)
            {
                return false;
            }

            var ca = GetCaCertificate();
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

        private void EnsureFreshCertificates()
        {
            var nowTicks = Environment.TickCount64;
            var lastCheck = Interlocked.Read(ref _lastCheckTicks);

            if (_currentCert != null && _cachedCaCert != null && TimeSpan.FromMilliseconds(nowTicks - lastCheck) < CheckInterval)
            {
                return;
            }

            Interlocked.Exchange(ref _lastCheckTicks, nowTicks);

            lock (_lock)
            {
                CheckAndReloadNodeCert();
                CheckAndReloadCaCert();
            }
        }

        private void CheckAndReloadNodeCert()
        {
            if (!File.Exists(_certPath) || !File.Exists(_keyPath)) return;

            var certTime = File.GetLastWriteTimeUtc(_certPath);
            var keyTime = File.GetLastWriteTimeUtc(_keyPath);
            var latest = certTime > keyTime ? certTime : keyTime;

            if (_currentCert == null || latest > _lastCertWriteTime)
            {
                try
                {
                    _currentCert = X509Certificate2.CreateFromPemFile(_certPath, _keyPath);
                    _lastCertWriteTime = latest;
                    _logger.LogInformation("Node certificate loaded/reloaded successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load node certificate from {Path}", _certPath);
                    if (_currentCert == null) throw;
                }
            }
        }

        private void CheckAndReloadCaCert()
        {
            if (string.IsNullOrEmpty(_caCertPath) || !File.Exists(_caCertPath)) return;

            var caTime = File.GetLastWriteTimeUtc(_caCertPath);
            if (_cachedCaCert == null || caTime > _lastCaWriteTime)
            {
                try
                {
                    _cachedCaCert = X509CertificateLoader.LoadCertificateFromFile(_caCertPath);
                    _lastCaWriteTime = caTime;
                    _logger.LogInformation("Cluster CA loaded/reloaded from {Path}", _caCertPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load CA certificate from {Path}", _caCertPath);
                }
            }
        }

        public void Dispose()
        {
            _currentCert?.Dispose();
            _cachedCaCert?.Dispose();
        }
    }
}