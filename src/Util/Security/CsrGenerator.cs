using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DryDock.Util.Security
{
    public static class CsrGenerator
    {
        public static (string CsrPem, ECDsa PrivateKey) CreateEcdsaCsr(
            string commonName,
            IEnumerable<string> dnsNames,
            IEnumerable<System.Net.IPAddress> ipAddresses)
        {
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={commonName}"),
                ecdsa,
                HashAlgorithmName.SHA256);

            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var dns in dnsNames)
            {
                sanBuilder.AddDnsName(dns);
            }

            foreach (var ip in ipAddresses)
            {
                sanBuilder.AddIpAddress(ip);
            }

            request.CertificateExtensions.Add(sanBuilder.Build());

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection
                    {
                    new("1.3.6.1.5.5.7.3.1"), // Server Authentication
                    new("1.3.6.1.5.5.7.3.2")  // Client Authentication
                    },
                    critical: false));

            byte[] csrBytes = request.CreateSigningRequest();
            string pem = PemEncoding.WriteString("CERTIFICATE REQUEST", csrBytes);

            return (pem, ecdsa);
        }
    }
}
