namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// IDelay — the wait seam. Production waits on the real Task.Delay; unit tests inject
// a controllable delay so a "wait N minutes then mark stable" is tested deterministically
// without sleeping (and without a real timer leaking into a unit test).
// -----------------------------------------------------------------------
public interface IDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken ct);
}

/// <summary>Production delay — the real, cancellable Task.Delay.</summary>
public sealed class SystemDelay : IDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}
