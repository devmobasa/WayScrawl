using System.Runtime.CompilerServices;
using WayScrawl.Core;

namespace WayScrawl.Application;

public static class StatusFeed
{
    public static async IAsyncEnumerable<string> LinesAsync(Func<CancellationToken, Task<DaemonStatus>> query,
        bool follow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? previous = null;
        do
        {
            DaemonStatus? status;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try { status = await query(timeout.Token); }
            catch (Exception error) when (error is DaemonUnavailableException or IOException or System.Text.Json.JsonException or System.Net.Sockets.SocketException) { status = null; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { status = null; }
            string line = WaybarStatus.Compose(status);
            if (line != previous) { previous = line; yield return line; }
            if (!follow) yield break;
            await Task.Delay(500, cancellationToken);
        } while (!cancellationToken.IsCancellationRequested);
    }
}
