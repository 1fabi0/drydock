namespace DryDock.Configuration
{
    public class DryDockOptions
    {
        public const string Section = "DryDock";

        public string CSIUnixSocketPath { get; set; } = "/var/lib/kubelet/plugins/csi-drydock.sock";
        public int VolumeSyncingPort { get; set; } = 50443;
        public string LocalStorageRoot { get; set; } = "/var/lib/drydock/volumes";
        public string CertPath { get; set; } = "/certs/tls.crt";
        public string KeyPath { get; set; } = "/certs/tls.key";
        public string CaCertPath { get; set; } = "/certs/ca.crt";
        public bool IsBackupEnabled { get; set; } = false;
        public string S3AccessKey { get; set; } = string.Empty;
        public string S3SecretKey { get; set; } = string.Empty;
        public string BackupS3Endpoint { get; set; } = string.Empty;
        public string BackupS3Bucket { get; set; } = string.Empty;
        public string NodeName { get; set; } = string.Empty;
        public string StorageClassName { get; set; } = "drydock";
        public int CapacityReportIntervalSeconds { get; set; } = 300;
        public string PluginVersion { get; set; } = "0.0.1";
        public string BackupCronExpression { get; set; } = "0 2 * * *";
    }
}