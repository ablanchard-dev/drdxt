using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// 30/09/2026 (boucle « la carte, prouvée », G4) : un réglage de la carte ne reste que s'il ne coûte rien en jeu. Les
// parties relevées avant et après se comparent sur le même jeu, par séparation nette (toutes les parties d'après
// sous toutes celles d'avant = pire), sans seuil inventé. Moins de trois parties d'un côté : pas encore jugé.
public sealed class ComparaisonDesPartiesTests
{
    private static readonly DateTimeOffset Reglage = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static PartieRelevee Partie(int jours, double low1, string jeu = "cod") =>
        new(jeu, Reglage.AddDays(jours), Reglage.AddDays(jours).AddMinutes(20), 10, 140, low1, BottleneckState.CpuBoundStrong);

    [Fact]
    public void Toutes_les_parties_d_apres_sous_toutes_celles_d_avant_c_est_pire()
    {
        var parties = new[] { Partie(-3, 90), Partie(-2, 92), Partie(-1, 91), Partie(1, 80), Partie(2, 82), Partie(3, 81) };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.Pire);
    }

    [Fact]
    public void Des_parties_qui_se_chevauchent_ne_prouvent_rien()
    {
        var parties = new[] { Partie(-3, 90), Partie(-2, 85), Partie(-1, 91), Partie(1, 88), Partie(2, 86), Partie(3, 92) };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.PasDeDifference);
    }

    [Fact]
    public void Toutes_au_dessus_c_est_mieux()
    {
        var parties = new[] { Partie(-3, 80), Partie(-2, 82), Partie(-1, 81), Partie(1, 90), Partie(2, 92), Partie(3, 91) };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.Mieux);
    }

    [Fact]
    public void Moins_de_trois_parties_d_un_cote_ce_n_est_pas_encore_juge()
    {
        var parties = new[] { Partie(-3, 90), Partie(-2, 92), Partie(-1, 91), Partie(1, 50), Partie(2, 52) };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.PasEncoreJuge);
    }

    [Fact]
    public void Un_autre_jeu_ne_compte_pas()
    {
        var parties = new[] { Partie(-3, 90), Partie(-2, 92), Partie(-1, 91), Partie(1, 50, "fortnite"), Partie(2, 52, "fortnite"), Partie(3, 51, "fortnite") };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.PasEncoreJuge,
            "comparer deux jeux mesurerait les jeux, pas le réglage");
    }
}
