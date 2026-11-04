using System.IO;
using System.Text.RegularExpressions;

namespace DryDock.Util
{
    public static class LinuxMountDetector
    {
        private const string DefaultMountsFile = "/proc/1/mounts";

        public static bool IsPathMounted(string targetPath, string volumeId, string mountsFilePath = DefaultMountsFile)
        {
            if (!File.Exists(mountsFilePath))
            {
                mountsFilePath = "/proc/mounts";
                if (!File.Exists(mountsFilePath))
                {
                    return false;
                }
            }

            string normalizedTargetPath = NormalizePath(targetPath);

            try
            {
                using var reader = new StreamReader(new FileStream(mountsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                string? line;

                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string mountSource = DecodeOctalEscapes(parts[0]);
                    string mountPoint = DecodeOctalEscapes(parts[1]);

                    string normalizedSource = NormalizePath(mountSource);
                    string normalizedMountPoint = NormalizePath(mountPoint);

                    if (string.Equals(normalizedMountPoint, normalizedTargetPath, StringComparison.Ordinal) ||
                        string.Equals(normalizedSource, normalizedTargetPath, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (normalizedMountPoint.StartsWith(normalizedTargetPath + "/", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (!string.IsNullOrEmpty(volumeId) &&
                        normalizedMountPoint.Contains($"/volumes/kubernetes.io~csi/{volumeId}", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return true;
            }

            return false;
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd('/');
        }

        private static string DecodeOctalEscapes(string input)
        {
            if (!input.Contains('\\')) return input;

            return Regex.Replace(input, @"\\([0-7]{3})", match =>
            {
                int code = Convert.ToInt32(match.Groups[1].Value, 8);
                return ((char)code).ToString();
            });
        }
    }
}