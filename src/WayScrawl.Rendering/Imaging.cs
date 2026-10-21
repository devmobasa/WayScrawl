using SkiaSharp;

namespace WayScrawl.Rendering;

public static class Imaging
{
    private static readonly SKSamplingOptions Smooth = new(SKCubicResampler.Mitchell);

    public static SKBitmap Decode(byte[] png) =>
        SKBitmap.Decode(png) ?? throw new InvalidDataException("Image could not be decoded.");

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return data.ToArray();
    }

    // Returns a new bitmap no larger than maxLongEdge; never upscales.
    public static SKBitmap Fit(SKBitmap source, int maxLongEdge)
    {
        double ratio = Math.Min(1, maxLongEdge / (double)Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        int height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, new SKRect(0, 0, width, height), Smooth);
        return result;
    }

    public static SKBitmap Crop(SKBitmap source, SKRectI rect)
    {
        var bounded = SKRectI.Intersect(rect, new SKRectI(0, 0, source.Width, source.Height));
        if (bounded.Width <= 0 || bounded.Height <= 0) throw new ArgumentOutOfRangeException(nameof(rect));
        var result = new SKBitmap(new SKImageInfo(bounded.Width, bounded.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, bounded, new SKRect(0, 0, bounded.Width, bounded.Height), Smooth);
        return result;
    }
}

public static class ContactSheet
{
    // Labelled grid of already-scaled frames, readable rather than merely small.
    public static byte[] Compose(IReadOnlyList<(SKBitmap Image, string Label)> frames, int cellWidth = 640, int maxColumns = 3)
    {
        if (frames.Count == 0) throw new ArgumentException("No frames.", nameof(frames));
        int columns = Math.Min(maxColumns, frames.Count);
        int rows = (frames.Count + columns - 1) / columns;
        double aspect = frames.Max(f => f.Image.Height / (double)f.Image.Width);
        int imageHeight = (int)Math.Round(cellWidth * aspect);
        const int label = 28, gap = 8;
        int width = columns * cellWidth + (columns + 1) * gap;
        int height = rows * (imageHeight + label) + (rows + 1) * gap;
        using var sheet = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(24, 26, 32));
        using var font = new SKFont(SKTypeface.Default, 17);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (int i = 0; i < frames.Count; i++)
        {
            int x = gap + i % columns * (cellWidth + gap);
            int y = gap + i / columns * (imageHeight + label + gap);
            var (image, caption) = frames[i];
            double scale = Math.Min(cellWidth / (double)image.Width, imageHeight / (double)image.Height);
            float w = (float)(image.Width * scale), h = (float)(image.Height * scale);
            using var source = SKImage.FromBitmap(image);
            canvas.DrawImage(source, SKRect.Create(x + (cellWidth - w) / 2, y, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
            canvas.DrawText(caption.Length > 70 ? caption[..69] + "…" : caption, x + 4, y + imageHeight + 20, SKTextAlign.Left, font, text);
        }
        return Imaging.EncodePng(sheet);
    }
}
