using System.Runtime.InteropServices;

namespace Drydock.Interop
{
    public static partial class LinuxMount
    {
        private const int MS_BIND = 4096;
        private const int MS_REC = 16384;

        [LibraryImport("libc", EntryPoint = "mount", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int MountSyscall(string source, string target, string filesystemtype, ulong mountflags, string? data);

        [LibraryImport("libc", EntryPoint = "umount2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int UnmountSyscall(string target, int flags);

        public static void BindMount(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            int result = MountSyscall(sourceDir, targetDir, "none", MS_BIND | MS_REC, null);
            if (result != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"Bind mount failed from '{sourceDir}' to '{targetDir}'. Errno: {errno}");
            }
        }

        public static void Unmount(string targetDir)
        {
            int result = UnmountSyscall(targetDir, 2);
            if (result != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno != 22) // EINVAL
                {
                    throw new InvalidOperationException($"Unmount failed for '{targetDir}'. Errno: {errno}");
                }
            }
        }
    }
}