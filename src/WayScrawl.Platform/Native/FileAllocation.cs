using System.Runtime.InteropServices;

namespace WayScrawl.Platform.Native;

// Linux's fixed-size statx ABI, checked against linux/stat.h. The kernel fills the entire
// 256-byte buffer; explicit offsets avoid libc stat's architecture-dependent layout.
public static partial class FileAllocation
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanoseconds;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ReadNative(int directoryFd, string path, int flags, uint mask, out Statx result);

    public static FileIdentity Identity(string path)
    {
        var stat = Read(path);
        return new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.ChangedSeconds, stat.ChangedNanoseconds);
    }

    internal static Statx Read(string path)
    {
        if (ReadNative(-100, path, 0x100, 0x7ff, out var value) != 0)
            throw new IOException($"statx failed for {path} (errno {Marshal.GetLastPInvokeError()}).");
        if ((value.Mask & 0x7c5) != 0x7c5 || (value.Mode & 0xf000) == 0xa000)
            throw new IOException("Missing inode accounting data or symbolic link: " + path);
        return value;
    }
}

public sealed record FileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode, long ChangedSeconds, uint ChangedNanoseconds)
{
    public bool SameInode(FileIdentity other) =>
        DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor && Inode == other.Inode;
}
