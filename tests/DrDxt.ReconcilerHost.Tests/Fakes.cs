using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Doublures en mémoire de l'hôte du réconciliateur. Aucune ne touche un vrai minuteur, le vrai
// SCM ou le Planificateur de tâches, ni le registre, ni n'élève : chaque effet est modélisé
// en mémoire pour que `dotnet test` soit hermétique et déterministe.
// -----------------------------------------------------------------------

/// <summary>Enregistre chaque changement journalisé via IDiagnosticsLog pour les assertions.</summary>
public sealed class RecordingDiagnostics : IDiagnosticsLog
{
    public List<(string tweakId, string from, string to, string actor)> Entries { get; } = new();

    public void Change(string tweakId, string from, string to, string actor)
        => Entries.Add((tweakId, from, to, actor));

    public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
}

/// <summary>Horloge UTC déterministe et avançable (miroir de celle du Reconciler).</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Magasin d'état annexe en mémoire. <see cref="FailLoad"/> / <see cref="FailSave"/>
/// forcent indépendamment l'op correspondante à échouer ; <see cref="SaveCount"/> prouve qu'un
/// Load raté n'a jamais atteint Save.</summary>
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

/// <summary>Fausse sonde de build de l'OS.</summary>
public sealed class FakeOsBuildProbe : IOsBuildProbe
{
    public string? Build { get; set; }
    public bool Fail { get; set; }

    public Result<string> ReadCurrentBuild()
        => Fail
            ? Result<string>.Fail(ErrorCodes.RegistryFail, "build read failed (test)")
            : Result<string>.Ok(Build ?? "22631.3447");
}

/// <summary>Faux magasin d'issue de démarrage : lit une série mutable ; les méthodes d'écriture
/// la mutent (incrément au démarrage, remise à zéro à la session stable). <see cref="Fail"/> fait
/// échouer chaque opération. Des compteurs laissent un test vérifier le nombre exact d'appels.</summary>
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
