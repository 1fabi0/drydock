using System.Formats.Tar;
using ZstdSharp;

namespace DryDock.Util
{
    public class TarZstdCompressor
    {
        public static async Task CompressDirectoryToStreamAsync(string sourceDirectory, Stream outputStream, CancellationToken ct)
        {        
            await using var compressionStream = new CompressionStream(outputStream, level: 3);
            await using var tarWriter = new TarWriter(compressionStream, leaveOpen: true);

            var dirQueue = new Queue<string>();
            dirQueue.Enqueue(sourceDirectory);

            while (dirQueue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string currentDir = dirQueue.Dequeue();

                foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                {
                    ct.ThrowIfCancellationRequested();
                    dirQueue.Enqueue(subDir);

                    string relativePath = Path.GetRelativePath(sourceDirectory, subDir).Replace('\\', '/');
                    var dirEntry = new PaxTarEntry(TarEntryType.Directory, $"{relativePath}/");
                    await tarWriter.WriteEntryAsync(dirEntry, ct);
                }

                foreach (var filePath in Directory.EnumerateFiles(currentDir))
                {
                    ct.ThrowIfCancellationRequested();

                    string relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
                    var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, relativePath);

                    await using var fileStream = new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 128 * 1024,
                        useAsync: true);

                    fileEntry.DataStream = fileStream;
                    await tarWriter.WriteEntryAsync(fileEntry, ct);
                }
            }

            await compressionStream.FlushAsync(ct);
        }
    }
}