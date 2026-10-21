using System.Text.Json;

namespace WayScrawl.Core;

public sealed record WindowSnapshot(string Address, string Class, PixelRect Rect, bool Focused, string? Title = null);

public static class WindowLayout
{
    public static WindowSnapshot[] At(IReadOnlyList<TimelineEvent> events, long atUs)
    {
        var item = events.LastOrDefault(e => e.AtUs <= atUs && e.Kind is "window.layout" or "telemetry.disconnected" or "telemetry.lease-expired");
        if (item?.Kind != "window.layout") return [];
        try { return JsonSerializer.Deserialize<WindowSnapshot[]>(item.Detail, WayScrawlJson.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static TimelineEvent WithoutTitles(TimelineEvent item) => item.Kind switch
    {
        "window.focus" or "window.open" => item with { Detail = item.Detail.Split(':', 2)[0] },
        "window.layout" => item with { Detail = JsonSerializer.Serialize(At([item], item.AtUs)
            .Where(w => w is not null).Select(w => w with { Title = null }).ToArray(), WayScrawlJson.Journal) },
        _ => item
    };

    public static PixelRect? Crop(IReadOnlyList<TimelineEvent> events, long atUs, int width, int height, PixelRect? previous)
    {
        var focused = At(events, atUs).FirstOrDefault(w => w is not null && w.Focused);
        if (focused is null || focused.Rect.Width <= 0 || focused.Rect.Height <= 0 ||
            (double)focused.Rect.Width * focused.Rect.Height >= 0.6 * width * height) return null;
        var rect = focused.Rect;
        int x = Math.Max(0, (int)Math.Floor(rect.X - width * 0.02));
        int y = Math.Max(0, (int)Math.Floor(rect.Y - height * 0.02));
        int right = Math.Min(width, (int)Math.Ceiling((long)rect.X + rect.Width + width * 0.02));
        int bottom = Math.Min(height, (int)Math.Ceiling((long)rect.Y + rect.Height + height * 0.02));
        if (right <= x || bottom <= y) return null;
        var next = new PixelRect(x, y, right - x, bottom - y);
        if (previous is { } old && old.X >= 0 && old.Y >= 0 && (long)old.X + old.Width <= width && (long)old.Y + old.Height <= height)
        {
            long intersection = (long)Math.Max(0, Math.Min(old.X + old.Width, next.X + next.Width) - Math.Max(old.X, next.X)) *
                Math.Max(0, Math.Min(old.Y + old.Height, next.Y + next.Height) - Math.Max(old.Y, next.Y));
            long union = (long)old.Width * old.Height + (long)next.Width * next.Height - intersection;
            if (union > 0 && intersection / (double)union > 0.7) return old;
        }
        return next;
    }

    public static TimelineEvent Scale(TimelineEvent item, double sx, double sy)
    {
        if (item.Kind != "window.layout" || sx == 1 && sy == 1) return item;
        var windows = At([item], item.AtUs).Select(w => w with { Rect = new((int)Math.Floor(w.Rect.X * sx), (int)Math.Floor(w.Rect.Y * sy),
            (int)Math.Ceiling(w.Rect.Width * sx), (int)Math.Ceiling(w.Rect.Height * sy)) }).ToArray();
        return item with { Detail = JsonSerializer.Serialize(windows, WayScrawlJson.Journal) };
    }
}
