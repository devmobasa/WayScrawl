using System.Text.Json;
using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Application;

// Runs speech recognition in a separate, killable process so a native or CUDA failure
// cannot take down the caller. An automatic runtime retries once on the CPU.
public sealed class WorkerTranscriber(string model, string language, string runtime, TimeSpan timeout) : ITranscriber
{
    public async Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(audioPath, runtime, cancellationToken);
        }
        catch (Exception error) when (runtime == "auto" && error is IOException or InvalidDataException or TimeoutException)
        {
            var retry = await RunAsync(audioPath, "cpu", cancellationToken);
            return retry with { Info = retry.Info with { Detail = retry.Info.Detail + " GPU attempt failed: " + error.Message } };
        }
    }

    private async Task<TranscriptionResult> RunAsync(string audioPath, string mode, CancellationToken cancellationToken)
    {
        string output = audioPath + "." + mode + ".transcript.json";
        File.Delete(output);
        var (file, prefix) = SelfCommand.Resolve();
        await using var worker = SupervisedProcess.Start(file,
            [.. prefix, "transcribe", audioPath, output, "--model", model, "--language", language, "--runtime", mode]);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            int code = await worker.Exited.WaitAsync(timer.Token);
            if (code != 0 || !File.Exists(output))
                throw new IOException($"Transcription worker ({mode}) exited with {code}: " +
                    ProcessRunner.LastLine(string.Join('\n', worker.RecentOutput())));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Transcription ({mode}) exceeded {timeout.TotalSeconds:0} s.");
        }
        return JsonSerializer.Deserialize<TranscriptionResult>(await File.ReadAllBytesAsync(output, cancellationToken),
            WayScrawlJson.Options) ?? throw new InvalidDataException("Empty transcription result.");
    }
}
