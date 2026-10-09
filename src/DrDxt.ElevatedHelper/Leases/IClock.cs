namespace DrDxt.ElevatedHelper.Leases;

// -----------------------------------------------------------------------
// IClock — the injectable time source for the lease watchdog.
//
// Lease expiry is computed against this clock, never DateTimeOffset.UtcNow
// directly, so tests advance a FakeClock and drive expiry deterministically with
// ZERO real waiting (no Thread.Sleep on wall-clock). Production wires SystemClock.
// -----------------------------------------------------------------------
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock — the real UTC wall-clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
