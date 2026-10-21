using System.Text.Json;

namespace WayScrawl.Core;

public static class WaybarStatus
{
    public static string Compose(DaemonStatus? status)
    {
        string text = "", css = status is null ? "offline" : "idle", tooltip = status is null ? "WayScrawl daemon not running" : "WayScrawl idle";
        if (status is not null)
        {
            bool recording = status.State is "Preparing" or "Recording";
            bool drawing = status.Draw is OverlayInputMode.Drawing or OverlayInputMode.Holding;
            if (recording)
            {
                text = (drawing ? "✎ " : "● ") + PasteText.Time(status.ElapsedUs);
                css = drawing ? status.Draw == OverlayInputMode.Holding ? "holding" : "drawing" : "recording";
            }
            if (status.Processing > 0)
            {
                text += (text.Length > 0 ? " · " : "") + status.Processing;
                if (!recording) css = "processing";
            }
            tooltip = $"WayScrawl {status.State} {PasteText.Time(status.ElapsedUs)} · processing {status.Processing}";
            if (status.LastError is { } error) tooltip += "\n" + error;
        }
        return JsonSerializer.Serialize(new { text, tooltip, @class = css }, WayScrawlJson.Line);
    }
}
