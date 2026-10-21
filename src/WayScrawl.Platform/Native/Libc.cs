using System.Runtime.InteropServices;

namespace WayScrawl.Platform.Native;

public static partial class Libc
{
    public const int SIGINT = 2;
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;
    private const int ClockMonotonic = 1;
    private const int FGetFd = 1;
    private const int FSetFd = 2;
    private const int FdCloExec = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int KillNative(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int ClockGetTime(int clock, out TimeSpec value);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int command, int argument);

    [LibraryImport("libc", EntryPoint = "getuid")]
    public static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinkNative(string existing, string created);

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint RealPathNative(string path, nint buffer);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void FreeNative(nint value);

    internal static string RealPath(string path)
    {
        nint value = RealPathNative(path, 0);
        if (value == 0) throw new IOException($"Cannot resolve path {path} (errno {Marshal.GetLastPInvokeError()}).");
        try { return Marshal.PtrToStringUTF8(value) ?? throw new IOException("Empty resolved path."); }
        finally { FreeNative(value); }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenDirectoryNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int SyncNative(int fd);

    public static void SyncFile(string path, Func<string, int>? syncError = null)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        Sync(handle.DangerousGetHandle().ToInt32(), path, syncError);
    }

    public static void SyncDirectory(string directory, Func<string, int>? syncError = null)
    {
        int fd = OpenDirectoryNative(directory, 0x10000 | 0x80000); // O_DIRECTORY | O_CLOEXEC, read-only.
        if (fd < 0) throw new IOException($"Cannot open directory for synchronization (errno {Marshal.GetLastPInvokeError()}).");
        try { Sync(fd, directory, syncError); }
        finally { CloseNative(fd); }
    }

    private static void Sync(int fd, string path, Func<string, int>? syncError)
    {
        int error = syncError?.Invoke(path) ?? 0;
        if (error == 0 && SyncNative(fd) != 0) error = Marshal.GetLastPInvokeError();
        if (error != 0) throw new IOException($"Cannot synchronize {path} (errno {error}).");
    }

    // False when the paths are on different filesystems or links are unsupported.
    public static bool TryHardLink(string existing, string created) => LinkNative(existing, created) == 0;

    public static bool Kill(int pid, int signal) => pid > 0 && KillNative(pid, signal) == 0;

    [LibraryImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static partial int PidfdOpenNative(int pid, uint flags);

    [LibraryImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static partial int PidfdSendSignalNative(int pidfd, int signal, nint info, uint flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int fd);

    // A pidfd names one process for its whole life; signalling it can never hit a recycled PID.
    public static int PidfdOpen(int pid) => PidfdOpenNative(pid, 0);
    public static bool PidfdSignal(int pidfd, int signal) => pidfd >= 0 && PidfdSendSignalNative(pidfd, signal, 0, 0) == 0;
    public static void Close(int fd) { if (fd >= 0) CloseNative(fd); }

    // CLOCK_MONOTONIC in microseconds: the basis gpu-screen-recorder's first-frame file uses.
    public static long MonotonicMicroseconds()
    {
        if (ClockGetTime(ClockMonotonic, out TimeSpec value) != 0)
            throw new InvalidOperationException("clock_gettime failed: errno " + Marshal.GetLastPInvokeError());
        return value.Seconds * 1_000_000 + value.Nanoseconds / 1_000;
    }

    // Lets a child started after this call inherit the descriptor at the same number.
    public static void ClearCloseOnExec(int fd)
    {
        int flags = Fcntl(fd, FGetFd, 0);
        if (flags < 0 || Fcntl(fd, FSetFd, flags & ~FdCloExec) < 0)
            throw new IOException("fcntl failed: errno " + Marshal.GetLastPInvokeError());
    }
}
