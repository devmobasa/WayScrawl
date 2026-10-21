namespace WayScrawl.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class CropPlanner
{
    // Context around recent ink in source pixels. Edges round outward so a mark at the
    // border is never truncated; the result stays inside the source. Null means the
    // full frame is already the best view.
    public static PixelRect? Plan(IEnumerable<Stroke> strokes, long atUs, int sourceWidth, int sourceHeight,
        long recentUs = 10_000_000, double maxAreaFraction = 0.6, IEnumerable<Pin>? pins = null, IReadOnlyList<TimelineEvent>? events = null, PixelRect? previous = null)
    {
        var stored = strokes.ToArray();
        var pinArray = (pins ?? []).ToArray();
        bool visible = stored.Any(s => s.VisibleAt(atUs)) || pinArray.Any(p => p.VisibleAt(atUs));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var stroke in stored.Concat(pinArray.Select(p => new Stroke(p.Id, p.GeometryId, "#ffffff", 22, [new(p.AtUs, p.X, p.Y)], p.RemovedUs))))
        {
            if (!stroke.VisibleAt(atUs) || stroke.Points[^1].AtUs < atUs - recentUs) continue;
            double pad = stroke.Width / 2;
            foreach (var p in stroke.Points)
            {
                minX = Math.Min(minX, p.X - pad); minY = Math.Min(minY, p.Y - pad);
                maxX = Math.Max(maxX, p.X + pad); maxY = Math.Max(maxY, p.Y + pad);
                any = true;
            }
        }
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;
        if (!any) return visible ? null : WindowLayout.Crop(events ?? [], atUs, sourceWidth, sourceHeight, previous);

        double width = maxX - minX, height = maxY - minY;
        double margin = Math.Max(Math.Max(width, height) * 0.35, Math.Min(sourceWidth, sourceHeight) * 0.08);
        minX -= margin; minY -= margin; maxX += margin; maxY += margin;
        // A crop narrower than a quarter of the source loses the surrounding UI.
        Grow(ref minX, ref maxX, sourceWidth / 4.0);
        Grow(ref minY, ref maxY, sourceHeight / 4.0);
        // Near an edge, slide the window inward rather than clipping away its context.
        Slide(ref minX, ref maxX, sourceWidth);
        Slide(ref minY, ref maxY, sourceHeight);

        int left = (int)Math.Floor(Math.Max(0, minX)), top = (int)Math.Floor(Math.Max(0, minY));
        int right = (int)Math.Ceiling(Math.Min(sourceWidth, maxX)), bottom = (int)Math.Ceiling(Math.Min(sourceHeight, maxY));
        if (right <= left || bottom <= top) return null;
        var rect = new PixelRect(left, top, right - left, bottom - top);
        return (double)rect.Width * rect.Height > maxAreaFraction * sourceWidth * sourceHeight ? null : rect;
    }

    private static void Slide(ref double min, ref double max, double size)
    {
        if (min < 0) { max = Math.Min(size, max - min); min = 0; }
        if (max > size) { min = Math.Max(0, min - (max - size)); max = size; }
    }

    private static void Grow(ref double min, ref double max, double minimum)
    {
        double missing = minimum - (max - min);
        if (missing <= 0) return;
        min -= missing / 2;
        max += missing / 2;
    }
}
