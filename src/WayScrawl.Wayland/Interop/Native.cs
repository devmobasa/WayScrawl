using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WayScrawl.Wayland.Interop;

// Adapted from SharpMarker's PosixInterop: shared memory for wl_shm, an eventfd for waking
// the dispatch thread, and poll over the display and wake descriptors.
internal static partial class Native
{
    private const int ProtRead = 0x1, ProtWrite = 0x2, MapShared = 0x01, MapPrivate = 0x02;
    private const uint MfdCloexec = 0x0001;
    private const int EfdCloexec = 0x80000, EfdNonblock = 0x800;
    private const short PollIn = 0x001, PollOut = 0x004, PollErr = 0x008, PollHup = 0x010, PollNval = 0x020;
    private const int Eintr = 4, Eagain = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    internal readonly record struct PollResult(bool DisplayReadable, bool DisplayWritable, bool DisplayFailed, bool WakeReadable);

    [LibraryImport("libc", EntryPoint = "memfd_create", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MemfdCreate(string name, uint flags);

    [LibraryImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    private static partial int Ftruncate(int fd, long length);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static partial nint Mmap(nint address, nuint length, int protection, int flags, int fd, nint offset);

    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static partial int Munmap(nint address, nuint length);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int fd);

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    private static partial int EventFd(uint initial, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint Write(int fd, void* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint Read(int fd, void* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int PollNative(PollFd* fds, nuint count, int timeout);

    internal static int CreateMemFd(string name, int size)
    {
        int fd = MemfdCreate(name, MfdCloexec);
        if (fd < 0) throw new IOException($"memfd_create failed (errno {Marshal.GetLastPInvokeError()}).");
        if (Ftruncate(fd, size) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            Close(fd);
            throw new IOException($"ftruncate failed (errno {error}).");
        }
        return fd;
    }

    internal static nint Map(int fd, int length)
    {
        nint pointer = Mmap(0, (nuint)length, ProtRead | ProtWrite, MapShared, fd, 0);
        if (pointer == -1) throw new IOException($"mmap failed (errno {Marshal.GetLastPInvokeError()}).");
        return pointer;
    }

    // Keymaps arrive as read-only shared memory; MAP_PRIVATE is required by wl_keyboard v7.
    internal static nint MapReadOnly(int fd, int length)
    {
        nint pointer = Mmap(0, (nuint)length, ProtRead, MapPrivate, fd, 0);
        if (pointer == -1) throw new IOException($"mmap failed (errno {Marshal.GetLastPInvokeError()}).");
        return pointer;
    }

    internal static void Unmap(nint address, int length)
    {
        if (address != 0 && Munmap(address, (nuint)length) != 0)
            throw new IOException($"munmap failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    internal static void Close(int fd)
    {
        if (fd < 0) return;
        while (CloseNative(fd) == -1 && Marshal.GetLastPInvokeError() == Eintr) { }
    }

    internal static int CreateEventFd()
    {
        int fd = EventFd(0, EfdCloexec | EfdNonblock);
        return fd >= 0 ? fd : throw new IOException($"eventfd failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    internal static unsafe void Signal(int fd)
    {
        ulong value = 1;
        if (Write(fd, &value, sizeof(ulong)) < 0 && Marshal.GetLastPInvokeError() != Eagain)
            throw new IOException($"eventfd write failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    internal static unsafe void Drain(int fd)
    {
        ulong value;
        if (Read(fd, &value, sizeof(ulong)) < 0 && Marshal.GetLastPInvokeError() != Eagain)
            throw new IOException($"eventfd read failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    internal static unsafe PollResult Poll(int display, int wake, bool wantWritable, int timeoutMs)
    {
        PollFd* fds = stackalloc PollFd[2];
        fds[0] = new PollFd { Fd = display, Events = (short)(PollIn | (wantWritable ? PollOut : 0)) };
        fds[1] = new PollFd { Fd = wake, Events = PollIn };
        long started = Stopwatch.GetTimestamp();
        int remaining = timeoutMs;
        while (true)
        {
            int result = PollNative(fds, 2, remaining);
            if (result >= 0) break;
            if (Marshal.GetLastPInvokeError() != Eintr) throw new IOException($"poll failed (errno {Marshal.GetLastPInvokeError()}).");
            if (timeoutMs >= 0)
            {
                remaining = Math.Max(0, timeoutMs - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (remaining == 0) { fds[0].Revents = 0; fds[1].Revents = 0; break; }
            }
        }
        return new((fds[0].Revents & PollIn) != 0, (fds[0].Revents & PollOut) != 0,
            (fds[0].Revents & (PollErr | PollHup | PollNval)) != 0, (fds[1].Revents & PollIn) != 0);
    }
}
