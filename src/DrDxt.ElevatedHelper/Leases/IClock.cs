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

/// <summary>Horloge de production : l'heure murale lue une fois, puis un chronomètre monotone. Une horloge système
/// reculée (synchronisation, réglage à la main) après la mort de l'App retarderait sinon d'autant le rendu d'un bail.</summary>
public sealed class SystemClock : IClock
{
    private readonly DateTimeOffset _depart;
    private readonly Func<TimeSpan> _ecoule;

    public SystemClock() : this(DateTimeOffset.UtcNow, Chronometre()) { }

    private static Func<TimeSpan> Chronometre()
    {
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        return () => chrono.Elapsed;
    }

    internal SystemClock(DateTimeOffset depart, Func<TimeSpan> ecoule) => (_depart, _ecoule) = (depart, ecoule);

    public DateTimeOffset UtcNow => _depart + _ecoule();
}
