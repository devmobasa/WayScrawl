using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using global::Wayland;
using global::Wayland.SourceGenerator;

namespace WayScrawl.Wayland.Interop;

// The display connection, owned by exactly one dispatch thread. Adapted from SharpMarker.
internal sealed unsafe class Display : IDisposable
{
    private bool disposed;

    private Display(nint handle) => Handle = handle;

    internal nint Handle { get; }

    // name: null for $WAYLAND_DISPLAY, a socket name, or an absolute socket path.
    internal static Display Connect(string? name)
    {
        // libwayland prefers an inherited WAYLAND_SOCKET over any name; refuse to consume it.
        if (Environment.GetEnvironmentVariable("WAYLAND_SOCKET") is { Length: > 0 })
            throw new WaylandException("WAYLAND_SOCKET is set; refusing to take over an inherited connection.");
        nint handle = LibWayland.wl_display_connect(name);
        return handle == 0
            ? throw new WaylandException($"wl_display_connect failed (errno {Marshal.GetLastPInvokeError()}).")
            : new Display(handle);
    }

    internal WlRegistry GetRegistry()
    {
        WlArgument* args = stackalloc WlArgument[] { WlArgument.NewId };
        nint registry = LibWayland.wl_proxy_marshal_array_constructor_versioned(Handle, 1, args,
            ref WlRegistry.WlInterface, (uint)WlRegistry.WlInterface.Version);
        return registry == 0 ? throw new WaylandException("wl_display.get_registry failed.")
            : new WlRegistry(registry, WlRegistry.WlInterface.Version);
    }

    // wl_display.sync: its done event proves the compositor processed every earlier request.
    internal WlCallback Sync()
    {
        WlArgument* args = stackalloc WlArgument[] { WlArgument.NewId };
        nint callback = LibWayland.wl_proxy_marshal_array_constructor_versioned(Handle, 0, args,
            ref WlCallback.WlInterface, 1);
        return callback == 0 ? throw new WaylandException("wl_display.sync failed.") : new WlCallback(callback, 1);
    }

    internal int Roundtrip() => LibWayland.CompleteDispatch(LibWayland.wl_display_roundtrip(Handle));
    internal int DispatchPending() => LibWayland.CompleteDispatch(LibWayland.wl_display_dispatch_pending(Handle));
    internal int PrepareRead() => LibWayland.wl_display_prepare_read(Handle);
    internal int ReadEvents() => LibWayland.wl_display_read_events(Handle);
    internal void CancelRead() => LibWayland.wl_display_cancel_read(Handle);
    internal int FileDescriptor => LibWayland.wl_display_get_fd(Handle);

    // Returns false when the socket buffer is full (EAGAIN); poll for writability then.
    internal bool Flush()
    {
        int result = LibWayland.wl_display_flush(Handle);
        if (result >= 0) return true;
        int error = Marshal.GetLastPInvokeError();
        return error == 11 ? false : throw new WaylandException($"wl_display_flush failed (errno {error}).");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        LibWayland.wl_display_disconnect(Handle);
    }
}

internal static unsafe class Proxies
{
    internal static T Bind<T>(this WlRegistry registry, uint name, uint advertised, IBindFactory<T> factory)
        where T : WlProxy
    {
        var info = factory.GetInterface();
        uint version = (uint)Math.Min(advertised, Math.Max(1, info->Version));
        string interfaceName = Marshal.PtrToStringAnsi(info->Name) ?? typeof(T).Name;
        using var utf8 = new Utf8Buffer(interfaceName);
        WlArgument* args = stackalloc WlArgument[] { name, utf8, version, WlArgument.NewId };
        nint handle = LibWayland.wl_proxy_marshal_array_constructor_versioned(registry.Handle, 0, args,
            ref Unsafe.AsRef<WlInterface>(info), version);
        return handle == 0 ? throw new WaylandException($"Binding {interfaceName} failed.") : factory.Create(handle, (int)version);
    }

    // Children inherit the parent's negotiated version; generated constructors would request
    // the XML maximum, which can exceed what the compositor bound (wl_seat 9 vs XML 10 here).
    internal static T Child<T>(WlProxy parent, uint opcode, IBindFactory<T> factory, ReadOnlySpan<WlArgument> arguments)
        where T : WlProxy
    {
        var info = factory.GetInterface();
        int version = Math.Min(parent.Version, info->Version);
        fixed (WlArgument* args = arguments)
        {
            nint handle = LibWayland.wl_proxy_marshal_array_constructor_versioned(parent.Handle, opcode, args,
                ref Unsafe.AsRef<WlInterface>(info), (uint)version);
            return handle == 0 ? throw new WaylandException("Creating a child proxy failed.") : factory.Create(handle, version);
        }
    }
}
