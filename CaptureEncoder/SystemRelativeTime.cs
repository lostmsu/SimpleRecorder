namespace CaptureEncoder;
using System;
using System.Diagnostics;

public struct SystemRelativeTime {
    public TimeSpan Value { get; init; }

    public static SystemRelativeTime Now {
        get {
            var ts = new Dirichlet.Numerics.Int128(Stopwatch.GetTimestamp());
            ts = ts * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
            return new() { Value = new((long)ts) };
        }
    }

    public static SystemRelativeTime FromEnvironmentTickCount(long milliseconds) {
        milliseconds -= initMilliseconds;
        return new() { Value = new(initTime.Value.Ticks + TimeSpan.TicksPerMillisecond * milliseconds) };
    }

    static readonly SystemRelativeTime initTime;
    static readonly long initMilliseconds;

    static SystemRelativeTime() {
        initMilliseconds = Environment.TickCount64;
        initTime = Now;
    }
}
