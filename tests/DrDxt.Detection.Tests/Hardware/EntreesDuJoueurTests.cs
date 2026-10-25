using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// L'activité du joueur, lue chaque seconde en partie. Un emplacement de manette vide coûte cher à interroger (XInput
// cherche l'appareil à chaque appel) : Microsoft conseille d'espacer ces vérifications de quelques secondes.
public sealed class EntreesDuJoueurTests
{
    private const uint Absente = 1167; // ERROR_DEVICE_NOT_CONNECTED

    [Fact]
    public void Un_emplacement_vide_n_est_pas_interroge_a_chaque_seconde()
    {
        var appels = new int[4];
        var maintenant = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);
        var entrees = new EntreesDuJoueur(i => { appels[i]++; return i == 0 ? (0u, 7u) : (Absente, 0u); }, () => maintenant);

        entrees.ActiveRecemment();
        maintenant = maintenant.AddSeconds(1);
        entrees.ActiveRecemment();

        appels[0].Should().Be(2, "la manette branchée se lit à chaque seconde");
        appels[1].Should().Be(1, "un emplacement vide attend avant d'être réinterrogé");

        maintenant += EntreesDuJoueur.EmplacementVide;
        entrees.ActiveRecemment();
        appels[1].Should().Be(2, "une manette branchée en cours de partie est vue après la pause");
    }
}
