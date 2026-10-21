using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using WayScrawl.Core;

namespace WayScrawl.Platform;

public static class Demo
{
    public static Task CreateAsync(string output, CancellationToken cancellationToken = default)
    {
        var geometry = new Geometry("g0", -320, 0, 320, 180, 320, 180);
        var bundle = new Bundle(Bundle.CurrentSchema, "demo-" + Guid.NewGuid().ToString("N"),
            true, DateTimeOffset.UtcNow, 4_000_000, [geometry], [],
            new Composition(CompositionMode.CapturedInk, "The synthetic fixture draws its ink into the frames."),
            [new("s1", "g0", "#ff3b30", 5,
                [new(1_000_000, 55, 115), new(1_150_000, 105, 87), new(1_300_000, 172, 116)])],
            [new(1_000_000, "stroke.begin", "Synthetic annotation s1", "overlay", "synthetic"),
             new(1_300_000, "stroke.end", "Synthetic annotation s1", "overlay", "synthetic"),
             new(3_000_000, "marker", "Synthetic review marker", "coordinator", "synthetic")],
            [],
            new TranscriptionInfo("synthetic", "none", "none", "none", "en", "none", "Fixture text, not speech recognition."),
            [new(500_000, 3_500_000, "Example narration. Align this panel with the heading.",
                TimingQuality.Synthetic)],
            [new(0, "frames/frame-00.png", "g0", "Synthetic initial view"),
             new(2_000_000, "frames/frame-01.png", "g0", "Synthetic completed annotation"),
             new(4_000_000, "frames/frame-02.png", "g0", "Synthetic final view")],
            [],
            [new("liveVideo", Availability.NotImplemented, "Not used by the demo"),
             new("microphone", Availability.NotImplemented, "Not used by the demo"),
             new("overlay", Availability.NotImplemented, "Not used by the demo"),
             new("passiveGlobalInput", Availability.Unsupported,
                 "Not supplied by the generic Wayland client model")],
            ["This is a schematic fixture, not a recording.",
             "No compatibility with upstream Scrawl import has been established."]);
        var assets = new Dictionary<string, byte[]>
        {
            ["contact-sheet.png"] = PngDemo.Make(960, 180, null),
            ["frames/frame-00.png"] = PngDemo.Make(320, 180, 0),
            ["frames/frame-01.png"] = PngDemo.Make(320, 180, 1),
            ["frames/frame-02.png"] = PngDemo.Make(320, 180, 2)
        };
        return BundleStore.WriteAsync(output, bundle, assets, null, cancellationToken);
    }
}

// Tiny dependency-free renderer for the synthetic demo bundle.
internal static class PngDemo
{
    public static byte[] Make(int width, int height, int? fixedStage)
    {
        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // PNG filter None.
                for (int x = 0; x < width; x++)
                {
                    int local = x % 320, stage = fixedStage ?? x / 320;
                    byte r = 23, g = 28, b = 40;
                    if (local > 16 && local < 304 && y > 18 && y < 45)
                        (r, g, b) = (49, 69, 88);
                    if (local > 35 + stage * 4 && local < 280 && y > 65 && y < 150)
                        (r, g, b) = (65, 145, 160);
                    if (stage > 0 &&
                        (Distance(local, y, 55, 115, 105, 87) < 2.5 ||
                         Distance(local, y, 105, 87, 172, 116) < 2.5))
                        (r, g, b) = (255, 59, 48);
                    zlib.WriteByte(r); zlib.WriteByte(g); zlib.WriteByte(b);
                }
            }
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; header[9] = 2; // 8-bit RGB.
        Chunk(png, "IHDR", header);
        Chunk(png, "tEXt", Encoding.ASCII.GetBytes("Description\0SYNTHETIC DEMO - no screen was recorded"));
        Chunk(png, "IDAT", pixels.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static double Distance(double x, double y, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(x - ax - t * dx, 2) + Math.Pow(y - ay - t * dy, 2));
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        byte[] name = Encoding.ASCII.GetBytes(type);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number); stream.Write(name); stream.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in name) crc = Update(crc, value);
        foreach (byte value in data) crc = Update(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        return crc;
    }
}
