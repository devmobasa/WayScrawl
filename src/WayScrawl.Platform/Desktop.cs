using System.Diagnostics;
using WayScrawl.Core;

namespace WayScrawl.Platform;

public static class Desktop
{
    // Best effort: a missing notification daemon never affects recording.
    public static async Task NotifyAsync(string title, string body, int timeoutMs = 3000)
    {
        try
        {
            await ProcessRunner.RunAsync("notify-send",
                ["-a", "WayScrawl", "-t", timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 "-h", "string:x-canonical-private-synchronous:wayscrawl", title, body],
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or OperationCanceledException) { }
    }

    public static async Task CopyTextAsync(string text, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("wl-copy")
        {
            // Own all three pipes: wl-copy forks a selection server. It must not inherit
            // an enclosing CLI caller's output pipes and prevent that caller seeing EOF.
            // Wait only for the launcher, never for EOF on the server's output.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--type");
        info.ArgumentList.Add("text/plain;charset=utf-8");
        using var process = Spawn.Start(info);
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        // wl-copy forks a server that owns the selection; the client exits promptly.
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new IOException("wl-copy exited with " + process.ExitCode + ".");
    }
}

// Clipboard publication is explicit; screenshot and narration text stays untrusted evidence.
public sealed class ClipboardSink(string mode = "paste") : IAgentSink
{
    public async Task SendAsync(string bundleDirectory, string destination, CancellationToken cancellationToken)
    {
        if (destination != "clipboard") throw new ArgumentException("Only the clipboard destination exists.", nameof(destination));
        string directory = Path.GetFullPath(bundleDirectory);
        var bundle = await BundleStore.ReadAsync(directory, cancellationToken);
        await Desktop.CopyTextAsync(mode == "path" ? Path.Combine(directory, "summary.md")
            : PasteText.Compose(bundle, directory), cancellationToken);
    }
}
