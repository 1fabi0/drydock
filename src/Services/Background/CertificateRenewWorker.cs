using DryDock.Configuration;
using DryDock.Contract;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Background
{
    public class CertificateRenewWorker : BackgroundService
    {
        private readonly ILogger _logger;
        private readonly ICertificateHolder _certHolder;
        private readonly ICertificateProvider _certProvider;
        private readonly DryDockOptions _options;

        private static readonly TimeSpan MaxSleepInterval = TimeSpan.FromHours(12);

        public CertificateRenewWorker(
            ILogger<CertificateRenewWorker> logger,
            ICertificateHolder certHolder,
            ICertificateProvider certProvider,
            IOptions<DryDockOptions> options)
        {
            _logger = logger;
            _certHolder = certHolder;
            _certProvider = certProvider;
            _options = options.Value;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Certificate Renewal Worker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var currentCert = _certHolder.GetCertificate();
                var now = DateTime.UtcNow;

                var expiryUtc = currentCert.NotAfter.ToUniversalTime();
                var renewDateTime = expiryUtc - TimeSpan.FromSeconds(_options.CertificateRenewBeforeSeconds);

                var timeUntilRenewal = renewDateTime - now;

                if (timeUntilRenewal <= TimeSpan.Zero)
                {
                    _logger.LogInformation("Certificate is due for renewal (Expires: {NotAfter} UTC). Renewing now...", expiryUtc);
                    await RenewAsync(stoppingToken);
                    continue;
                }

                var sleepDuration = timeUntilRenewal > MaxSleepInterval ? MaxSleepInterval : timeUntilRenewal;

                _logger.LogDebug("mTLS certificate valid until {Expiry} UTC. Next check in {Hours:F1}h.",
                    expiryUtc, sleepDuration.TotalHours);

                try
                {
                    await Task.Delay(sleepDuration, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (DateTime.UtcNow >= renewDateTime)
                {
                    await RenewAsync(stoppingToken);
                }
            }
        }

        private async Task RenewAsync(CancellationToken ct)
        {
            try
            {
                _logger.LogInformation("Requesting renewed mTLS certificate from cert-manager...");
                var (cert, ca) = await _certProvider.RequestNewCertificateAsync(ct);
                _certHolder.SwapCertificates(cert, ca);
                _logger.LogInformation("mTLS certificate successfully rotated in memory. New expiry: {NotAfter} UTC", cert.NotAfter);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to renew mTLS certificate. Will retry in 15 minutes.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
            }
        }
    }
}