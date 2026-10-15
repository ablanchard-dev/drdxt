namespace DrDxt.ElevatedHelper.Leases;

// -----------------------------------------------------------------------
// IClock : la source de temps injectable du chien de garde des baux.
//
// L'échéance d'un bail se calcule sur cette horloge, jamais directement sur
// DateTimeOffset.UtcNow : les tests font avancer un FakeClock et mènent l'échéance de façon
// déterministe, sans aucune attente réelle (pas de Thread.Sleep sur l'horloge murale). La
// production branche SystemClock.
// -----------------------------------------------------------------------
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Horloge de production : la vraie horloge murale UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
