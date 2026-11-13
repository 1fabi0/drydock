using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DryDock.Configuration;
using DryDock.Contract;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Certificate
{
    public class CertificateFileCache : ICertificateFileCache
    {
        private readonly ILogger _logger;
        private readonly string _certFilePath;
        private readonly string _certKeyFilePath;
        private readonly string _caCertFilePath;

        public CertificateFileCache(IOptions<DryDockOptions> options, ILogger<CertificateFileCache> logger)
        {
            _logger = logger;
            _certFilePath = options.Value.CertPath;
            _certKeyFilePath = options.Value.KeyPath;
            _caCertFilePath = options.Value.CaCertPath;
        }

        public X509Certificate2? ReadCaCertificateFromFile()
        {
            if (!File.Exists(_caCertFilePath))
            {
                return null;
            }

            try
            {
                var caCertPem = File.ReadAllText(_caCertFilePath);
                return X509Certificate2.CreateFromPem(caCertPem);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read cached CA certificate from '{Path}'.", _caCertFilePath);
                return null;
            }
        }

        public X509Certificate2? ReadCertificateFromFile()
        {
            if (!File.Exists(_certFilePath) || !File.Exists(_certKeyFilePath))
            {
                return null;
            }

            try
            {
                var certPem = File.ReadAllText(_certFilePath);
                var keyPem = File.ReadAllText(_certKeyFilePath);

                using var cert = X509Certificate2.CreateFromPem(certPem);
                using var privateKey = ECDsa.Create();
                privateKey.ImportFromPem(keyPem);

                var fullCert = cert.CopyWithPrivateKey(privateKey);

                // Nur verwenden, wenn es noch mindestens 24h gültig ist
                if (fullCert.NotAfter <= DateTime.UtcNow.AddHours(24))
                {
                    _logger.LogInformation("Cached certificate at '{Path}' is expired or near expiry ({NotAfter} UTC). Ignoring cache.",
                        _certFilePath, fullCert.NotAfter);
                    fullCert.Dispose();
                    return null;
                }

                return fullCert;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read cached certificate from '{Path}' or its private key from '{KeyPath}'.", _certFilePath, _certKeyFilePath);
                return null;
            }
        }

        public void WriteCaCertificateToFile(X509Certificate2 caCert)
        {
            EnsureDirectoryExists(_caCertFilePath);
            var caCertPem = caCert.ExportCertificatePem();
            WriteFileAtomic(_caCertFilePath, caCertPem, isPrivateKey: false);
        }

        public void WriteCertificateToFile(X509Certificate2 cert)
        {
            using var privateKey = cert.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("Certificate does not have an associated ECDSA private key.");

            EnsureDirectoryExists(_certFilePath);
            EnsureDirectoryExists(_certKeyFilePath);

            var certPem = cert.ExportCertificatePem();
            var keyPem = privateKey.ExportECPrivateKeyPem();

            // Zuerst den Private Key mit chmod 0600 schreiben, dann das Public Cert
            WriteFileAtomic(_certKeyFilePath, keyPem, isPrivateKey: true);
            WriteFileAtomic(_certFilePath, certPem, isPrivateKey: false);
        }

        private static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }

        private static void WriteFileAtomic(string targetPath, string content, bool isPrivateKey)
        {
            string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, content);

            if (!OperatingSystem.IsWindows())
            {
                var mode = isPrivateKey
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite // 0600 (nur Root/Owner)
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead; // 0644

                File.SetUnixFileMode(tempPath, mode);
            }

            // Atomares Umbenennen auf POSIX-Dateisystemen
            File.Move(tempPath, targetPath, overwrite: true);
        }
    }
}