using System.Net;
using System.Security.Cryptography.X509Certificates;
using Amazon.S3;
using DryDock.Configuration;
using DryDock.Contract;
using DryDock.Services.Background;
using DryDock.Services.Certificate;
using DryDock.Services.Csi;
using DryDock.Services.Storage;
using DryDock.Util;
using KubeOps.Abstractions.Builder;
using KubeOps.Operator;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace DryDock
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var drydockConfig = builder.Configuration.GetSection(DryDockOptions.Section);
            builder.Services.Configure<DryDockOptions>(drydockConfig);
            var options = drydockConfig.Get<DryDockOptions>() ?? new DryDockOptions();

            builder.WebHost.UseQuic(quicOptions =>
            {
                quicOptions.MaxBidirectionalStreamCount = 200;
            });

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                if (File.Exists(options.CSIUnixSocketPath))
                {
                    File.Delete(options.CSIUnixSocketPath);
                }
                serverOptions.ListenUnixSocket(options.CSIUnixSocketPath, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });

                serverOptions.ListenAnyIP(options.VolumeSyncingPort, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http3;
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificateSelector = (_, _) =>
                            listenOptions.ApplicationServices.GetRequiredService<ICertificateHolder>().GetCertificate();
                        httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                        httpsOptions.ClientCertificateValidation = (cert, chain, errors) =>
                        {
                            return listenOptions.ApplicationServices.GetRequiredService<ICertificateHolder>().ValidateRemoteCertificate(cert);
                        };
                    });
                });
            });

            builder.Services.AddKubernetesOperator(kubeOptions =>
            {
                kubeOptions.LeaderElectionType = LeaderElectionType.None;
                kubeOptions.Name = "drydock";
            });

            builder.Services.AddSingleton<ICertificateHolder, CertificateHolder>();
            builder.Services.AddSingleton<IStorageManager, LocalStorageManager>();
            builder.Services.AddHttpClient<IPeerSyncClient, PeerSyncClient>(client =>
            {
                client.DefaultRequestVersion = HttpVersion.Version30;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                client.Timeout = TimeSpan.FromMinutes(10);
            }).ConfigureCACertificate();

            if (options.IsBackupEnabled)
            {
                builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
                    options.S3AccessKey,
                    options.S3SecretKey,
                    new AmazonS3Config
                    {
                        ServiceURL = options.BackupS3Endpoint,
                        ForcePathStyle = true
                    }));
                builder.Services.AddSingleton<IBackupClient, S3BackupClient>();
                builder.Services.AddHostedService<BackupWorker>();
            }

            builder.Services.AddSingleton<IBackupClient, NoOpBackupClient>();

            builder.Services.AddGrpc();
            builder.Services.AddControllers();
            builder.Services.AddHostedService<CapacityReporterWorker>();

            var host = builder.Build();

            host.Lifetime.ApplicationStarted.Register(() =>
            {
                if (File.Exists(options.CSIUnixSocketPath) && OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(options.CSIUnixSocketPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                }
            });

            host.MapGrpcService<CsiIdentityService>();
            host.MapGrpcService<CsiNodeService>();
            host.MapGrpcService<CsiControllerService>();
            host.MapControllers();

            await host.RunAsync();

        }
    }
}