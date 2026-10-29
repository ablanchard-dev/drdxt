namespace DrDxt.ElevatedHelper.Leases;

// IClock : l'horloge du chien de garde des baux, jamais DateTimeOffset.UtcNow en direct : les tests avancent un FakeClock
// sans attente réelle ; la production branche SystemClock.
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
