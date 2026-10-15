namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// IDelay : la couture d'attente. La production attend sur le vrai Task.Delay ; les tests
// unitaires injectent une attente pilotable pour tester « attendre N minutes puis marquer
// stable » de façon déterministe, sans dormir (et sans vrai minuteur dans un test unitaire).
// -----------------------------------------------------------------------
public interface IDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken ct);
}

/// <summary>Attente de production : le vrai Task.Delay, annulable.</summary>
public sealed class SystemDelay : IDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}
