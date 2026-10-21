using global::Wayland;
using SkiaSharp;
using WayScrawl.Wayland.Interop;

namespace WayScrawl.Wayland;

// One wl_shm buffer whose mapped bytes Skia draws into. Only wl_buffer.release returns it.
internal sealed class ShmBuffer : IDisposable
{
    private readonly int fd;
    private readonly nint data;
    private readonly int length;
    private bool disposed;

    internal ShmBuffer(WlShm shm, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "Buffers must fit the 64 MP limit.");
        Width = width;
        Height = height;
        int stride = checked(width * 4);
        length = checked(stride * height);
        fd = Native.CreateMemFd("wayscrawl-overlay", length);
        try
        {
            data = Native.Map(fd, length);
            Pool = shm.CreatePool(fd, length);
            Buffer = Pool.CreateBuffer(0, width, height, stride, WlShm.FormatEnum.Argb8888);
            // wl_shm ARGB8888 is little-endian BGRA in memory, premultiplied.
            Bitmap = new SKBitmap();
            if (!Bitmap.InstallPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), data, stride))
                throw new InvalidOperationException("Skia could not borrow the shared memory.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int Width { get; }
    internal int Height { get; }
    internal WlShmPool Pool { get; } = null!;
    internal WlBuffer Buffer { get; } = null!;
    internal SKBitmap Bitmap { get; } = null!;
    internal bool Busy { get; set; }
    internal bool Disposed => disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Bitmap?.Dispose();
        Buffer?.Destroy();
        Pool?.Destroy();
        Native.Unmap(data, length);
        Native.Close(fd);
    }
}

// A fixed set of equally sized buffers. A resize retires the old set; its busy buffers are
// destroyed only after the compositor releases them.
internal sealed class ShmBufferSet : IDisposable
{
    private readonly List<ShmBuffer> buffers = [];
    private readonly OverlayDiagnostics diagnostics;
    private bool retired;

    internal ShmBufferSet(WlShm shm, int width, int height, int count, Action released, OverlayDiagnostics diagnostics)
    {
        this.diagnostics = diagnostics;
        Width = width;
        Height = height;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var buffer = new ShmBuffer(shm, width, height);
                buffer.Buffer.Events = new ReleaseEvents(this, buffer, released);
                buffers.Add(buffer);
                diagnostics.Created(1);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int Width { get; }
    internal int Height { get; }
    internal bool AllReleased => buffers.All(b => !b.Busy);

    internal ShmBuffer? Acquire() => retired ? null : buffers.FirstOrDefault(b => !b.Busy);

    internal void Retire()
    {
        retired = true;
        foreach (var buffer in buffers.Where(b => !b.Busy)) Destroy(buffer);
    }

    private void Destroy(ShmBuffer buffer)
    {
        if (buffer.Disposed) return;
        buffer.Dispose();
        diagnostics.Destroyed(1);
    }

    // After a disconnect the compositor owns nothing; otherwise call only when all are released.
    public void Dispose()
    {
        retired = true;
        foreach (var buffer in buffers) Destroy(buffer);
    }

    private sealed class ReleaseEvents(ShmBufferSet owner, ShmBuffer buffer, Action released) : WlBuffer.IEvents
    {
        public void OnRelease()
        {
            buffer.Busy = false;
            if (owner.retired) owner.Destroy(buffer);
            else released();
        }
    }
}
