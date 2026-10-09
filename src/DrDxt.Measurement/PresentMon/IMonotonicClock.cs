using System.Diagnostics;

namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Monotonic clock seam so the duration-bounded capture loop can be driven
/// deterministically in tests (no real wall-clock waiting).
/// </summary>
public interface IMonotonicClock
{
    /// <summary>Time elapsed since the last <see cref="Restart"/>.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Resets <see cref="Elapsed"/> to zero and starts counting.</summary>
    void Restart();
}

/// <summary>Production clock backed by <see cref="Stopwatch"/>.</summary>
public sealed class StopwatchClock : IMonotonicClock
{
    private readonly Stopwatch _sw = new();
    public TimeSpan Elapsed => _sw.Elapsed;
    public void Restart() => _sw.Restart();
}
