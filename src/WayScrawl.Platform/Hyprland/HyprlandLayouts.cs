using System.Text.Json;
using WayScrawl.Core;

namespace WayScrawl.Platform.Hyprland;

public static class HyprlandLayouts
{
    public static string Compose(string clientsJson, string monitorsJson, string activeJson, Geometry geometry, bool titles)
    {
        using var clients = JsonDocument.Parse(clientsJson);
        using var monitors = JsonDocument.Parse(monitorsJson);
        using var active = JsonDocument.Parse(activeJson);
        string output = geometry.Source["monitor:".Length..];
        var monitor = monitors.RootElement.EnumerateArray().First(m => m.GetProperty("name").GetString() == output);
        int id = monitor.GetProperty("id").GetInt32();
        var workspaces = new HashSet<int> { monitor.GetProperty("activeWorkspace").GetProperty("id").GetInt32() };
        if (monitor.TryGetProperty("specialWorkspace", out var special) && special.GetProperty("id").GetInt32() is int sid && sid != 0) workspaces.Add(sid);
        string focused = active.RootElement.TryGetProperty("address", out var address) ? address.GetString() ?? "" : "";
        string Text(JsonElement value, string key)
        {
            string text = value.TryGetProperty(key, out var field) ? field.GetString() ?? "" : "";
            return text.Length > 160 ? text[..160] : text;
        }
        var windows = new List<WindowSnapshot>();
        foreach (var client in clients.RootElement.EnumerateArray())
        {
            if (!client.GetProperty("mapped").GetBoolean() || client.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean() ||
                client.GetProperty("monitor").GetInt32() != id || !workspaces.Contains(client.GetProperty("workspace").GetProperty("id").GetInt32())) continue;
            var at = client.GetProperty("at"); var size = client.GetProperty("size");
            var from = geometry.ToSource(new(at[0].GetDouble(), at[1].GetDouble()));
            var to = geometry.ToSource(new(at[0].GetDouble() + size[0].GetDouble(), at[1].GetDouble() + size[1].GetDouble()));
            int x = (int)Math.Clamp(Math.Floor(from.X), 0, geometry.PixelWidth), y = (int)Math.Clamp(Math.Floor(from.Y), 0, geometry.PixelHeight);
            int right = (int)Math.Clamp(Math.Ceiling(to.X), 0, geometry.PixelWidth), bottom = (int)Math.Clamp(Math.Ceiling(to.Y), 0, geometry.PixelHeight);
            if (right <= x || bottom <= y) continue;
            string name = Text(client, "address");
            windows.Add(new(name, Text(client, "class"), new(x, y, right - x, bottom - y), name == focused, titles ? Text(client, "title") : null));
        }
        if (windows.Count > 512) throw new InvalidDataException("Window layout exceeds 512 mapped windows.");
        return JsonSerializer.Serialize(windows.OrderBy(w => w.Address, StringComparer.Ordinal), WayScrawlJson.Journal);
    }
}
