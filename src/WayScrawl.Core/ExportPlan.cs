using System.Globalization;

namespace WayScrawl.Core;

public sealed record ExportPlan(PixelRect Crop, int Width, int Height)
{
    public static ExportPlan Create(Bundle bundle, int width, int height, string crop = "auto", int size = 1920)
    {
        if (size is < 2 or > 7680 || width < 2 || height < 2) throw new ArgumentException("Export size must be 2–7680 and source dimensions at least 2.");
        PixelRect rect = new(0, 0, width, height);
        if (crop == "auto")
        {
            var regions = bundle.Keyframes.Where(k => k.Crop is not null).Select(k => k.Crop!).ToArray();
            if (regions.Length > 0)
            {
                int x = regions.Min(r => r.X), y = regions.Min(r => r.Y);
                long right = regions.Max(r => (long)r.X + r.Width), bottom = regions.Max(r => (long)r.Y + r.Height);
                if (right > width || bottom > height) throw new ArgumentException("Manifest crop exceeds the media dimensions.");
                rect = new(x, y, (int)right - x, (int)bottom - y);
            }
        }
        else if (crop != "full")
        {
            string[] fields = crop.Split(',');
            if (fields.Length != 4 || fields.Any(f => !int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                throw new ArgumentException("Crop must be auto, full or x,y,w,h.");
            int[] n = fields.Select(f => int.Parse(f, CultureInfo.InvariantCulture)).ToArray();
            rect = new(n[0], n[1], n[2], n[3]);
        }
        if (rect.X < 0 || rect.Y < 0 || rect.Width < 2 || rect.Height < 2 || (long)rect.X + rect.Width > width || (long)rect.Y + rect.Height > height)
            throw new ArgumentException("Crop must lie inside the source and be at least 2×2 pixels.");
        // Round dimensions down for H.264, preserving requested origin and avoiding upscale.
        rect = rect with { Width = rect.Width / 2 * 2, Height = rect.Height / 2 * 2 };
        double ratio = Math.Min(1, size / (double)Math.Max(rect.Width, rect.Height));
        return new(rect, Math.Max(2, (int)(rect.Width * ratio) / 2 * 2), Math.Max(2, (int)(rect.Height * ratio) / 2 * 2));
    }
}
