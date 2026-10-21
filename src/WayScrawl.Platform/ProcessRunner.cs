using System.Diagnostics;
using System.Text;

namespace WayScrawl.Platform;

public sealed record ProcessResult(int ExitCode, byte[] Output, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output);
}

// One-shot helper for short tools (ffprobe, ffmpeg, hyprctl). Cancellation kills the child.
public static class ProcessRunner
{
    private const int MaxErrorChars = 16_384;

    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments,
        CancellationToken cancellationToken, long maxOutputBytes = 256L * 1024 * 1024,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = Start(file, arguments, environment);
        var error = ReadBoundedAsync(process.StandardError);
        using var output = new MemoryStream();
        try
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > maxOutputBytes)
                    throw new InvalidDataException(file + " output exceeded " + maxOutputBytes + " bytes.");
                output.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryKill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, output.ToArray(), await error);
    }

    public static async Task<ProcessResult> RunCheckedAsync(string file, IEnumerable<string> arguments,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await RunAsync(file, arguments, cancellationToken, environment: environment);
        if (result.ExitCode != 0)
            throw new IOException($"{file} exited with {result.ExitCode}: {LastLine(result.Error)}");
        return result;
    }

    public static Process Start(string file, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;
        var process = Spawn.Start(info);
        process.StandardInput.Close();
        return process;
    }

    public static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxErrorChars) text.Remove(0, text.Length - MaxErrorChars);
        }
        return text.ToString();
    }

    public static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "no diagnostic output";
}
