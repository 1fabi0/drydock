using System.Formats.Tar;
using ZstdSharp;

namespace DryDock.Util
{
    public static class TarZstdExtractor
    {
        public static async Task ExtractTarZstStreamAsync(
        Stream incomingCompressedStream,
        string targetDirectory,
        CancellationToken ct)
        {
            string fullTargetDir = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(fullTargetDir);

            await using var decompressionStream = new DecompressionStream(incomingCompressedStream);
            await using var tarReader = new TarReader(decompressionStream, leaveOpen: true);

            TarEntry? entry;
            while ((entry = await tarReader.GetNextEntryAsync(cancellationToken: ct)) != null)
            {
                ct.ThrowIfCancellationRequested();

                string destinationPath = Path.GetFullPath(Path.Combine(fullTargetDir, entry.Name));
                if (!destinationPath.StartsWith(fullTargetDir, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Path Traversal Detected: {entry.Name} tries to extract outside of {fullTargetDir}");
                }

                if (entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    string? parentDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    if (entry.DataStream != null)
                    {
                        await using var fileStream = new FileStream(
                            destinationPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 128 * 1024,
                            useAsync: true);

                        await entry.DataStream.CopyToAsync(fileStream, ct);
                    }
                }
            }
        }
    }
}