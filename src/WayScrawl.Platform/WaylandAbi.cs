using System.Runtime.InteropServices;

namespace WayScrawl.Platform;

// ABI groundwork only. No protocol metadata tables or generated request wrappers
// are supplied; no native interface support is implied by these declarations.
[StructLayout(LayoutKind.Explicit)]
public struct WlArgument
{
    [FieldOffset(0)] public int Signed;
    [FieldOffset(0)] public uint Unsigned;
    [FieldOffset(0)] public int Fixed24_8;
    [FieldOffset(0)] public nint Pointer;
    [FieldOffset(0)] public uint NewId;
    [FieldOffset(0)] public int FileDescriptor;
}

[StructLayout(LayoutKind.Sequential)]
public struct WlArray
{
    public nuint Size;
    public nuint Allocation;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct WlMessage
{
    public nint Name;
    public nint Signature;
    public nint Types;
}

[StructLayout(LayoutKind.Sequential)]
public struct WlInterface
{
    public nint Name;
    public int Version;
    public int MethodCount;
    public nint Methods;
    public int EventCount;
    public nint Events;
}

internal static partial class WaylandRequests
{
    // Fixed-arity API; generated wrappers must supply owned, valid metadata.
    // This declaration is not called by WayScrawl and must not be used with guessed opcodes.
    [LibraryImport("libwayland-client.so.0", EntryPoint = "wl_proxy_marshal_array_flags")]
    internal static unsafe partial nint MarshalArrayFlags(nint proxy, uint opcode,
        nint interfaceMetadata, uint version, uint flags, WlArgument* arguments);
}
