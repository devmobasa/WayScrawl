using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using WayScrawl.Core;

namespace WayScrawl.Platform.Hyprland;

// A separate socket and Lua table keep ephemeral modifier controls independent from telemetry.
// Only the requested hold key is bound. No cursor position or key timestamp is returned.
public sealed class HyprlandHoldControl(string key, HyprlandInstance? instance = null) : IHoldControl
{
    private string token = Guid.NewGuid().ToString("N");
    private long lastSequence;
    public string Token => token;

    public async IAsyncEnumerable<HoldControlEvent> ReadAsync(ISessionClock clock,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string socket = HyprlandIpc.EventSocket(instance) ?? throw new InvalidOperationException("Not running under Hyprland.");
        using var stopRenewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? renewal = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), lease.Token);
                await using var stream = new NetworkStream(connection, ownsSocket: false);
                token = Guid.NewGuid().ToString("N");
                lastSequence = 0;
                await EvalAsync(Script(token, key), lease.Token);
                lease.CancelAfter(TimeSpan.FromSeconds(14));
                renewal ??= RenewAsync(stopRenewal.Token, lease);
                await foreach (string line in LinesAsync(stream, lease.Token, cancellationToken))
                {
                    if (line.StartsWith("configreloaded>>", StringComparison.Ordinal))
                    {
                        yield return new(false); // Release before the potentially slow IPC call.
                        token = Guid.NewGuid().ToString("N");
                        lastSequence = 0;
                        await EvalAsync(Script(token, key), lease.Token);
                        lease.CancelAfter(TimeSpan.FromSeconds(14));
                    }
                    else if (Parse(line) is { } item) yield return item;
                }
                if (cancellationToken.IsCancellationRequested) yield break;
                if (lease.IsCancellationRequested)
                {
                    yield return new(false, "The hold control lease could not be renewed; pointer drawing was released.");
                    yield break;
                }
                yield return new(false);
                await Task.Delay(200, cancellationToken);
            }
        }
        finally
        {
            await stopRenewal.CancelAsync();
            if (renewal is not null) await renewal.ContinueWith(_ => { }, TaskScheduler.Default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await HyprlandIpc.EvalAsync(StopScript(token), timeout.Token, instance); }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
        }
    }

    public HoldControlEvent? Parse(string line)
    {
        const string prefix = "custom>>wayscrawl-hold-v1:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = line[prefix.Length..].Split(':');
        if (parts.Length != 3 || parts[0] != token || parts[2] is not ("down" or "up") ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) || sequence <= lastSequence) return null;
        lastSequence = sequence;
        return new(parts[2] == "down");
    }

    private async Task RenewAsync(CancellationToken cancellationToken, CancellationTokenSource lease)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            string renewing = token;
            try
            {
                await EvalAsync(RenewScript(renewing), cancellationToken);
                if (renewing == token) lease.CancelAfter(TimeSpan.FromSeconds(14));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
        }
    }

    private async Task EvalAsync(string script, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await HyprlandIpc.EvalAsync(script, timeout.Token, instance); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Hyprland hold IPC timed out.", error); }
    }

    private static async IAsyncEnumerable<string> LinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken,
        CancellationToken ownerCancellation)
    {
        byte[] buffer = new byte[8192];
        var line = new List<byte>();
        bool overflow = false;
        while (true)
        {
            int count;
            try { count = await stream.ReadAsync(buffer, cancellationToken); }
            catch (IOException) { yield break; }
            catch (OperationCanceledException) when (!ownerCancellation.IsCancellationRequested) { yield break; }
            if (count == 0) yield break;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] != '\n')
                {
                    if (line.Count < 8192) line.Add(buffer[i]); else overflow = true;
                    continue;
                }
                if (!overflow) yield return Encoding.UTF8.GetString(line.ToArray());
                line.Clear(); overflow = false;
            }
        }
    }

    public static string StopScript(string token) => $"if WayScrawlHold and WayScrawlHold.version == 1 then WayScrawlHold.stop(\"{token}\") end";
    public static string RenewScript(string token) => $"if not WayScrawlHold or WayScrawlHold.version ~= 1 or not WayScrawlHold.renew(\"{token}\", 15) then error(\"hold lease not renewed\") end";

    // The compositor's unsafe bind removal is never used. Existing callbacks are token/lease
    // gated; successful individual binds are remembered so a partial installation can retry.
    public static string Script(string token, string key, int seconds = 15)
    {
        if (key is not ("Shift_L" or "Shift_R" or "Control_L" or "Control_R" or "Alt_L" or "Alt_R" or "Super_L" or "Super_R"))
            throw new ArgumentException("The hold key must be a supported Shift, Control, Alt or Super modifier keysym.", nameof(key));
        return """
        if not WayScrawlHold or WayScrawlHold.version ~= 1 then
          local state = { token = "", key = "", seq = 0, alive_until = 0, down = {}, up = {} }
          local function emit(key, direction)
            if state.key ~= key or state.token == "" or os.time() > state.alive_until then return end
            state.seq = state.seq + 1
            hl.dispatch(hl.dsp.event("wayscrawl-hold-v1:" .. state.token .. ":" .. state.seq .. ":" .. direction))
          end
          WayScrawlHold = {
            version = 1,
            start = function(token, key, seconds)
              state.token, state.key, state.alive_until = "", "", 0
              if not state.down[key] then
                local ok, err = pcall(hl.bind, key, function() emit(key, "down") end,
                  { non_consuming = true, ignore_mods = true, transparent = true })
                if not ok then error(err) end
                state.down[key] = true
              end
              if not state.up[key] then
                local mods = { Shift_L = "SHIFT", Shift_R = "SHIFT", Control_L = "CTRL", Control_R = "CTRL", Alt_L = "ALT", Alt_R = "ALT", Super_L = "SUPER", Super_R = "SUPER" }
                local upkey = mods[key] and (mods[key] .. " + " .. key) or key
                local ok, err = pcall(hl.bind, upkey, function() emit(key, "up") end,
                  { non_consuming = true, ignore_mods = true, transparent = true, release = true })
                if not ok then error(err) end
                state.up[key] = true
              end
              state.token, state.key, state.seq, state.alive_until = token, key, 0, os.time() + seconds
            end,
            renew = function(token, seconds) if state.token == token then state.alive_until = os.time() + seconds; return true end return false end,
            stop = function(token) if state.token == token then state.token, state.alive_until = "", 0 end end
          }
        end
        WayScrawlHold.start("TOKEN", "HOLDKEY", SECONDS)
        """.Replace("TOKEN", token).Replace("HOLDKEY", key).Replace("SECONDS", seconds.ToString(CultureInfo.InvariantCulture));
    }
}
