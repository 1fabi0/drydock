using System.Runtime.InteropServices;

namespace DryDock.Interop
{
    public static partial class FileSystem
    {
        public static long GetLinuxAvailableBytes(string path, ILogger logger)
        {
            if (StatVfs(path, out var stat) == 0)
            {
                return (long)stat.f_bavail * (long)stat.f_frsize;
            }

            int errno = Marshal.GetLastPInvokeError();
            logger.LogWarning("statvfs Syscall failed for {Path}. Errno: {Errno}.  Using DriveInfo as fallback.", path, errno);

            var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
            return drive.AvailableFreeSpace;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct StatVfsStruct
        {
            public ulong f_bsize;    // Filesystem block size
            public ulong f_frsize;   // Fragment size
            public ulong f_blocks;   // Size of fs in f_frsize units
            public ulong f_bfree;    // Number of free blocks
            public ulong f_bavail;   // Number of free blocks for unprivileged users
            public ulong f_files;    // Number of inodes
            public ulong f_ffree;    // Number of free inodes
            public ulong f_favail;   // Number of free inodes for unprivileged users
            public ulong f_fsid;     // Filesystem ID
            public ulong f_flag;     // Mount flags
            public ulong f_namemax;  // Maximum filename length
            public fixed ulong __f_spare[6];
        }

        [LibraryImport("libc", EntryPoint = "statvfs", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int StatVfs(string path, out StatVfsStruct buf);

    }
}


