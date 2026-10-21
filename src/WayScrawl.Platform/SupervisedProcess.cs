using System.Diagnostics;
using WayScrawl.Platform.Native;

namespace WayScrawl.Platform;

// Owns exactly one child. Signals go to that PID only, never to a process name.
public sealed class SupervisedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Queue<string> recent = new();
    private readonly int maxLines;
    private readonly Task pumps;
    private readonly object gate = new();
    private int pidfd;

    private SupervisedProcess(Process process, int maxLines)
    {
        this.process = process;
        this.maxLines = maxLines;
        // Opened right after start; afterwards signals target this process even if its PID
        // is reaped and recycled.
        pidfd = Libc.PidfdOpen(process.Id);
        pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        Exited = WaitAsync();
    }

    public int Pid => process.Id;
    public string Name { get; private init; } = "";
    public Task<int> Exited { get; }
    public bool HasExited => Exited.IsCompleted;
    // Set before SIGTERM/SIGKILL is sent, so it is reliable once Exited completes.
    public bool Escalated { get; private set; }

    // Invoked on a reader thread; handlers must be quick and must not throw.
    public event Action<string>? Line;

    // Supervises a child started elsewhere (for example with an inherited descriptor).
    public static SupervisedProcess Adopt(Process process, string name, int maxLines = 200)
    {
        process.StandardInput.Close();
        return new SupervisedProcess(process, maxLines) { Name = name };
    }

    public static SupervisedProcess Start(string file, IEnumerable<string> arguments,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null,
        int maxLines = 200)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;
        var process = Spawn.Start(info);
        process.StandardInput.Close();
        return new SupervisedProcess(process, maxLines) { Name = Path.GetFileName(file) };
    }

    public IReadOnlyList<string> RecentOutput()
    {
        lock (gate) return recent.ToArray();
    }

    public bool Signal(int signal)
    {
        lock (gate)
        {
            if (process.HasExited) return false;
            // Without pidfd support, fall back to the PID; HasExited narrows the reuse window.
            return pidfd >= 0 ? Libc.PidfdSignal(pidfd, signal) : Libc.Kill(process.Id, signal);
        }
    }

    // SIGINT lets recorders finalize their container; escalate only after bounded waits.
    public async Task<int> StopAsync(TimeSpan graceful, TimeSpan forced)
    {
        if (!HasExited && Signal(Libc.SIGINT) &&
            await Task.WhenAny(Exited, Task.Delay(graceful)) == Exited)
            return await Exited;
        Escalated = !process.HasExited;
        if (!HasExited && Signal(Libc.SIGTERM) &&
            await Task.WhenAny(Exited, Task.Delay(forced)) == Exited)
            return await Exited;
        Signal(Libc.SIGKILL);
        return await Exited;
    }

    public async ValueTask DisposeAsync()
    {
        if (!HasExited) await StopAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        lock (gate)
        {
            Libc.Close(pidfd);
            pidfd = -1;
        }
        process.Dispose();
    }

    private async Task Pump(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 4096) line = line[..4096];
            lock (gate)
            {
                recent.Enqueue(line);
                while (recent.Count > maxLines) recent.Dequeue();
            }
            Line?.Invoke(line);
        }
    }

    private async Task<int> WaitAsync()
    {
        await process.WaitForExitAsync();
        // A grandchild that inherited the pipes can hold them open after the child exits;
        // drain briefly, but never let it delay the exit beyond a bound.
        await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2)));
        return process.ExitCode;
    }
}
