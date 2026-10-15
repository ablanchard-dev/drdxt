using System.Diagnostics;

namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Couture d'horloge monotone pour mener de façon déterministe, en test, la boucle de capture
/// bornée en durée (sans vraie attente sur l'horloge murale).
/// </summary>
public interface IMonotonicClock
{
    /// <summary>Temps écoulé depuis le dernier <see cref="Restart"/>.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Remet <see cref="Elapsed"/> à zéro et commence à compter.</summary>
    void Restart();
}

/// <summary>Horloge de production adossée à <see cref="Stopwatch"/>.</summary>
public sealed class StopwatchClock : IMonotonicClock
{
    private readonly Stopwatch _sw = new();
    public TimeSpan Elapsed => _sw.Elapsed;
    public void Restart() => _sw.Restart();
}
