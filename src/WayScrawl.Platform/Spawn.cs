using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WayScrawl.Platform;

// Every child process starts through this gate. Ordinary starts share it; a start that hands a
// descriptor to its child takes it exclusively, so the briefly inheritable duplicate can never
// leak into an unrelated child started at the same moment.
public static partial class Spawn
{
    private static readonly ReaderWriterLockSlim Gate = new();
    private const int FDupfdCloexec = 1030, FSetfd = 2;

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int command, int argument);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    public static Process Start(ProcessStartInfo info)
    {
        Gate.EnterReadLock();
        try { return Process.Start(info) ?? throw new IOException("Could not start " + info.FileName + "."); }
        finally { Gate.ExitReadLock(); }
    }

    // Starts the child with `fd` duplicated to an inheritable descriptor; returns that number,
    // which the child sees. The parent's duplicate is closed again before the gate opens.
    public static (Process Process, int ChildFd) StartWithDescriptor(ProcessStartInfo info, SafeHandle fd)
    {
        Gate.EnterWriteLock();
        int duplicate = -1;
        bool added = false;
        try
        {
            fd.DangerousAddRef(ref added);
            duplicate = Fcntl((int)fd.DangerousGetHandle(), FDupfdCloexec, 10);
            if (duplicate < 0 || Fcntl(duplicate, FSetfd, 0) < 0)
                throw new IOException($"Could not prepare the descriptor (errno {Marshal.GetLastPInvokeError()}).");
            for (int i = 0; i < info.ArgumentList.Count; i++)
                info.ArgumentList[i] = info.ArgumentList[i].Replace("{fd}", duplicate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = Process.Start(info) ?? throw new IOException("Could not start " + info.FileName + ".");
            return (process, duplicate);
        }
        finally
        {
            if (duplicate >= 0) Close(duplicate);
            if (added) fd.DangerousRelease();
            Gate.ExitWriteLock();
        }
    }
}
