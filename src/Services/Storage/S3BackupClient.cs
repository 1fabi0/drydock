using Amazon.S3;
using Amazon.S3.Model;
using DryDock.Configuration;
using DryDock.Contract;
using DryDock.Util;
using Microsoft.Extensions.Options;

namespace DryDock.Services.Storage
{
    public class S3BackupClient : IBackupClient
    {
        private readonly IAmazonS3 _s3Client;
        private readonly ILogger _logger;
        private readonly string _bucketName;

        public S3BackupClient(IAmazonS3 s3Client, ILogger<S3BackupClient> logger, IOptions<DryDockOptions> options)
        {
            _s3Client = s3Client;
            _logger = logger;
            _bucketName = options.Value.BackupS3Bucket;
        }

        public async Task BackupVolumeAsync(string volumeId, string localSourcePath, CancellationToken cancellationToken)
        {
            string s3Key = $"volumes/{volumeId}.tar.zst";
            string stagingDir = $"{localSourcePath}.staging-{Guid.NewGuid():N}";

            try
            {
                _logger.LogInformation("Backing up volume {VolumeId} to S3 bucket {Bucket}...", volumeId, _bucketName);

                if (!Directory.Exists(localSourcePath))
                {
                    throw new DirectoryNotFoundException($"Volume directory '{localSourcePath}' does not exist.");
                }

                using var tarZstdStream = File.Create(stagingDir);
                await TarZstdCompressor.CompressDirectoryToStreamAsync(localSourcePath, tarZstdStream, cancellationToken);

                var request = new PutObjectRequest
                {
                    BucketName = _bucketName,
                    Key = s3Key,
                    InputStream = tarZstdStream
                };

                await _s3Client.PutObjectAsync(request, cancellationToken);

                _logger.LogInformation("Volume {VolumeId} successfully backed up to S3.", volumeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while backing up volume {VolumeId} to S3", volumeId);
                throw;
            }
        }

        public async Task DownloadVolumeAsync(string volumeId, string localSourcePath, CancellationToken cancellationToken)
        {
            string stagingDir = $"{localSourcePath}.staging-{Guid.NewGuid():N}";
            string s3Key = $"volumes/{volumeId}.tar.zst";

            try
            {
                _logger.LogInformation("Load S3 Backup from Bucket {Bucket} for {VolumeId}...",_bucketName, volumeId);

                var request = new GetObjectRequest
                {
                    BucketName = _bucketName,
                    Key = s3Key
                };

                using var response = await _s3Client.GetObjectAsync(request, cancellationToken);
                await using var s3Stream = response.ResponseStream;

                await TarZstdExtractor.ExtractTarZstStreamAsync(s3Stream, stagingDir, cancellationToken);

                if (Directory.Exists(localSourcePath))
                {
                    Directory.Delete(localSourcePath, recursive: true);
                }
                Directory.Move(stagingDir, localSourcePath);

                _logger.LogInformation("Volume {VolumeId} successfully restored from S3 backup.", volumeId);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogError("No S3 backup found for volume {VolumeId} (404 Not Found).", volumeId);
                throw new FileNotFoundException($"No S3 backup for volume '{volumeId}' available.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while downloading S3 backup for {VolumeId}", volumeId);
                throw;
            }
            finally
            {
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
                }
            }
        }
    }
}