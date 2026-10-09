using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// In-memory test doubles for the reconciler HOST. None touch a real timer, the
// real SCM/Task Scheduler, the registry, or perform elevation — every effect is
// modelled in memory so `dotnet test` is hermetic and deterministic.
// -----------------------------------------------------------------------

/// <summary>Records every change logged through IDiagnosticsLog for assertions.</summary>
public sealed class RecordingDiagnostics : IDiagnosticsLog
{
    public List<(string tweakId, string from, string to, string actor)> Entries { get; } = new();

    public void Change(string tweakId, string from, string to, string actor)
        => Entries.Add((tweakId, from, to, actor));

    public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
}

/// <summary>A deterministic, advanceable UTC clock (mirrors the Reconciler one).</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>FINDING 1 — a controllable entitlement gate for the tick tests. Defaults to
/// ENTITLED (Ok) so existing load→reconcile tests are unchanged; set <see cref="Entitled"/>
/// false to exercise the fail-closed "lapsed entitlement → do not reapply" path. Records
/// how many times it was consulted.</summary>
public sealed class FakeEntitlementGate : IEntitlementGate
{
    public bool Entitled { get; set; } = true;
    public int Calls { get; private set; }

    public Task<Result> EnsureReapplyEntitledAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Entitled
            ? Result.Ok()
            : Result.Fail(ErrorCodes.ManifestTampered, "manifest does not verify (test)"));
    }
}

/// <summary>In-memory sidecar state store. <see cref="FailLoad"/> / <see cref="FailSave"/>
/// independently force the corresponding op to Fail; <see cref="SaveCount"/> proves a
/// failed Load never reached Save.</summary>
public sealed class InMemoryReconcilerStateStore : IReconcilerStateStore
{
    private ReconcilerState? _state;
    public ReconcilerState? Saved => _state;
    public bool FailLoad { get; set; }
    public bool FailSave { get; set; }
    public int SaveCount { get; private set; }

    public InMemoryReconcilerStateStore(ReconcilerState? initial = null) => _state = initial;

    public Task<Result<ReconcilerState>> LoadAsync(CancellationToken ct)
        => Task.FromResult(FailLoad
            ? Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, "load failed (test)")
            : Result<ReconcilerState>.Ok(_state ?? ReconcilerState.Default));

    public Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct)
    {
        if (FailSave)
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "save failed (test)"));
        _state = state;
        SaveCount++;
        return Task.FromResult(Result.Ok());
    }
}

/// <summary>Fake OS-build probe.</summary>
public sealed class FakeOsBuildProbe : IOsBuildProbe
{
    public string? Build { get; set; }
    public bool Fail { get; set; }

    public Result<string> ReadCurrentBuild()
        => Fail
            ? Result<string>.Fail(ErrorCodes.RegistryFail, "build read failed (test)")
            : Result<string>.Ok(Build ?? "22631.3447");
}

/// <summary>Fake boot-outcome store: reads a mutable streak; the write methods mutate
/// it (increment on boot-started, reset on session-stable). <see cref="Fail"/> fails
/// every operation. Counters let a test assert exact call counts.</summary>
public sealed class FakeBootOutcomeStore : IBootOutcomeStore
{
    public int Streak { get; set; }
    public bool Fail { get; set; }
    public int MarkBootStartedCount { get; private set; }
    public int MarkSessionStableCount { get; private set; }

    public Task<Result<int>> GetUncleanStreakAsync(CancellationToken ct)
        => Task.FromResult(Fail
            ? Result<int>.Fail(ErrorCodes.IoFailure, "boot store failed (test)")
            : Result<int>.Ok(Streak));

    public Task<Result> MarkBootStartedAsync(CancellationToken ct)
    {
        if (Fail) return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "boot store failed (test)"));
        MarkBootStartedCount++;
        Streak++;
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> MarkSessionStableAsync(CancellationToken ct)
    {
        if (Fail) return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "boot store failed (test)"));
        MarkSessionStableCount++;
        Streak = 0;
        return Task.FromResult(Result.Ok());
    }
}
