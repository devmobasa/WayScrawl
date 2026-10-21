using SkiaSharp;
using WayScrawl.Core;

namespace WayScrawl.Rendering;

// The single stroke renderer used by the live overlay and by exporters, so ink looks the same.
// Coordinates are source pixels; callers set the canvas matrix for any other target space.
public static class InkRenderer
{
    public static SKColor ParseColor(string color) =>
        SKColor.TryParse(color, out var parsed) ? parsed : SKColors.Red;

    public static void Draw(SKCanvas canvas, Stroke stroke) =>
        Draw(canvas, stroke.Points, ParseColor(stroke.Color), (float)stroke.Width);

    public static void DrawAt(SKCanvas canvas, Stroke stroke, long atUs)
    {
        if (stroke.VisibleAt(atUs))
            Draw(canvas, stroke.Points.TakeWhile(p => p.AtUs <= atUs).ToArray(), ParseColor(stroke.Color), (float)stroke.Width);
    }

    public static void Draw(SKCanvas canvas, IReadOnlyList<InkPoint> points, SKColor color, float width)
    {
        if (points.Count == 0) return;
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Style = SKPaintStyle.Stroke
        };
        if (points.Count == 1)
        {
            // A tap is a visible dot rather than an invisible zero-length path.
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawCircle((float)points[0].X, (float)points[0].Y, width / 2, paint);
            return;
        }
        using var builder = new SKPathBuilder();
        builder.MoveTo((float)points[0].X, (float)points[0].Y);
        // Quadratic segments through midpoints smooth pointer jitter without overshoot.
        for (int i = 1; i < points.Count - 1; i++)
        {
            float mx = (float)((points[i].X + points[i + 1].X) / 2), my = (float)((points[i].Y + points[i + 1].Y) / 2);
            builder.QuadTo((float)points[i].X, (float)points[i].Y, mx, my);
        }
        builder.LineTo((float)points[^1].X, (float)points[^1].Y);
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);
    }

    public static void DrawPin(SKCanvas canvas, Pin pin, float sourcePerLogical = 1, byte alpha = 255)
    {
        float x = (float)pin.X, y = (float)pin.Y, radius = 11 * sourcePerLogical;
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(25, 25, 28, alpha) };
        canvas.DrawCircle(x, y, radius, paint);
        paint.Color = SKColors.White.WithAlpha(alpha);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.5f * sourcePerLogical;
        canvas.DrawCircle(x, y, radius, paint);
        paint.Style = SKPaintStyle.Fill;
        using var font = new SKFont(SKTypeface.Default, 13 * sourcePerLogical);
        string number = pin.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        canvas.DrawText(number, x, y - (font.Metrics.Ascent + font.Metrics.Descent) / 2, SKTextAlign.Center, font, paint);
    }

    public static SKRect Bounds(IReadOnlyList<InkPoint> points, double width)
    {
        float pad = (float)(width / 2 + 2);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, (float)p.X); minY = Math.Min(minY, (float)p.Y);
            maxX = Math.Max(maxX, (float)p.X); maxY = Math.Max(maxY, (float)p.Y);
        }
        return points.Count == 0 ? SKRect.Empty : new SKRect(minX - pad, minY - pad, maxX + pad, maxY + pad);
    }
}
