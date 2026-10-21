using System.Runtime.InteropServices;
using WayScrawl.Core;

namespace WayScrawl.Platform;

public static partial class WaylandProbe
{
    [LibraryImport("libwayland-client.so.0", EntryPoint = "wl_display_connect",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint Connect(string? name);

    [LibraryImport("libwayland-client.so.0", EntryPoint = "wl_display_disconnect")]
    private static partial void Disconnect(nint display);

    public static Capability ConnectOnce()
    {
        if (!OperatingSystem.IsLinux())
            return new("waylandConnection", Availability.Unsupported, "Linux is required.");
        if (Environment.GetEnvironmentVariable("WAYLAND_SOCKET") is not null)
            return new("waylandConnection", Availability.Unknown,
                "Skipped: WAYLAND_SOCKET would transfer ownership of an inherited FD.");
        string? name = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (string.IsNullOrEmpty(name))
            return new("waylandConnection", Availability.Unknown, "WAYLAND_DISPLAY is absent.");
        try
        {
            nint display = Connect(name);
            if (display == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                return new("waylandConnection", Availability.Unknown, $"Connect failed; errno={error}.");
            }
            try
            {
                return new("waylandConnection", Availability.Available,
                    "Socket connection only. Registry, protocol support and capture permission untested.");
            }
            finally { Disconnect(display); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException
                                     or BadImageFormatException)
        {
            return new("waylandConnection", Availability.Unknown,
                "Native library could not be loaded: " + error.GetType().Name);
        }
    }
}
