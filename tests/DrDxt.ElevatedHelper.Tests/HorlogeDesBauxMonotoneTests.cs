using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// L'échéance d'un bail se calcule sur cette horloge. Sur l'heure murale, une horloge reculée après la mort de l'App
// (synchronisation, réglage à la main) gardait un décalage de carte ou un ventilateur fixe posé d'autant. Elle ne lit
// l'heure murale qu'une fois, puis avance au chronomètre monotone.
public sealed class HorlogeDesBauxMonotoneTests
{
    [Fact]
    public void Elle_avance_au_chronometre_et_ne_relit_jamais_l_heure_murale()
    {
        var depart = new DateTimeOffset(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);
        var ecoule = TimeSpan.Zero;
        var horloge = new SystemClock(depart, () => ecoule);

        horloge.UtcNow.Should().Be(depart);
        ecoule = TimeSpan.FromSeconds(30);
        horloge.UtcNow.Should().Be(depart.AddSeconds(30));
    }

    [Fact]
    public void Celle_de_production_ne_recule_jamais()
    {
        var horloge = new SystemClock();
        var avant = horloge.UtcNow;
        horloge.UtcNow.Should().BeOnOrAfter(avant);
    }
}
