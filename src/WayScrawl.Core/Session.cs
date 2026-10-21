using System.Diagnostics;

namespace WayScrawl.Core;

public enum SessionState { Idle, Preparing, Recording, Finalizing, Ready, Cancelled, Failed }

public sealed class SessionMachine
{
    // The coordinator owns this object on one serialized execution lane.
    public SessionState State { get; private set; } = SessionState.Idle;

    public bool IsTerminal => State is SessionState.Ready or SessionState.Cancelled or SessionState.Failed;

    public void MoveTo(SessionState next)
    {
        bool allowed = (State, next) switch
        {
            (SessionState.Idle, SessionState.Preparing) => true,
            (SessionState.Preparing, SessionState.Recording) => true,
            (SessionState.Recording, SessionState.Finalizing) => true,
            (SessionState.Finalizing, SessionState.Ready) => true,
            (SessionState.Preparing or SessionState.Recording or SessionState.Finalizing,
                SessionState.Cancelled or SessionState.Failed) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Cannot transition {State} -> {next}.");
        State = next;
    }
}

public interface ISessionClock
{
    long ElapsedUs { get; }
    // CLOCK_MONOTONIC microseconds at session time zero, for mapping recorder timestamps.
    long OriginMonotonicUs { get; }
}

public sealed class SessionClock : ISessionClock
{
    private readonly long origin = Stopwatch.GetTimestamp();

    // On Linux, Stopwatch reads CLOCK_MONOTONIC; the check harness verifies that assumption.
    public static long MonotonicUs(long timestamp) => Stopwatch.Frequency % 1_000_000 == 0
        ? timestamp / (Stopwatch.Frequency / 1_000_000)
        : (long)(timestamp * (1_000_000d / Stopwatch.Frequency));

    public long ElapsedUs => Stopwatch.GetElapsedTime(origin).Ticks / 10;
    public long OriginMonotonicUs => MonotonicUs(origin);
}

public sealed class ManualClock(long originMonotonicUs = 0) : ISessionClock
{
    public long ElapsedUs { get; set; }
    public long OriginMonotonicUs { get; } = originMonotonicUs;
}
