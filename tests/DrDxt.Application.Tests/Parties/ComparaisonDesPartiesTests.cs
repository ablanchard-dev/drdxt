using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Un réglage de la carte ne reste que s'il ne coûte rien en jeu. Les
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

    // Avant : la médiane des 1 % low de chaque fenêtre (parties écrites avant le calcul sur toutes les images), plus haute en
    // général ; après : le 1 % low de toutes les images. Le « pire » venait de l'estimateur, pas du réglage.
    [Fact]
    public void Deux_estimateurs_du_1_pour_cent_low_ne_se_comparent_pas()
    {
        var parties = new[] { Partie(-3, 90), Partie(-2, 92), Partie(-1, 91) }
            .Concat(new[] { Partie(1, 80), Partie(2, 82), Partie(3, 81) }.Select(p => p with { Low1SurToutesLesImages = true })).ToList();

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.PasComparable);
        ComparaisonDesParties.Bilan(parties, Reglage).Verdict.Should().Be(VerdictEnJeu.PasComparable);
    }

    // Des parties de l'ancien estimateur parmi celles d'avant ne comptent pas ; assez du même estimateur, et le juge tranche.
    [Fact]
    public void Seules_les_parties_du_meme_estimateur_se_comparent()
    {
        static PartieRelevee N(PartieRelevee p) => p with { Low1SurToutesLesImages = true };
        var parties = new[] { Partie(-5, 60), Partie(-4, 61), N(Partie(-3, 90)), N(Partie(-2, 92)), N(Partie(-1, 91)),
            N(Partie(1, 80)), N(Partie(2, 82)), N(Partie(3, 81)) };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.Pire, "les deux parties à 60-61 sont de l'autre estimateur");
        ComparaisonDesParties.Bilan(parties, Reglage).Should().Match<BilanDuChangement>(b => b.Verdict == VerdictEnJeu.Pire && b.Avant == 3);
    }

    // Un levier de latence (HAGS, mode Jeu) coûte souvent un peu d'images : jugé sur le 1 % low, une latence nettement plus
    // basse passait pour « moins bien », et le retrait automatique le défaisait.
    [Fact]
    public void Un_levier_de_latence_se_juge_sur_la_latence_quand_les_parties_la_portent()
    {
        static PartieRelevee L(PartieRelevee p, double ms) => p with { LatenceAffichageMedianMs = ms };
        var parties = new[] { L(Partie(-3, 90), 30), L(Partie(-2, 92), 31), L(Partie(-1, 91), 29),
            L(Partie(1, 80), 20), L(Partie(2, 82), 21), L(Partie(3, 81), 19) };

        var latence = ComparaisonDesParties.Bilan(parties, Reglage, surLaLatence: true);
        latence.Should().Match<BilanDuChangement>(b => b.Verdict == VerdictEnJeu.Mieux && b.SurLaLatence
            && b.LatenceAvant == 30 && b.LatenceApres == 20);
        ComparaisonDesParties.Bilan(parties, Reglage).Verdict.Should().Be(VerdictEnJeu.Pire, "témoin : sur les images, c'est pire");
    }

    // Aucune partie ne porte la latence : le 1 % low juge, et le bilan dit que ce n'est pas la latence.
    [Fact]
    public void Sans_latence_lue_un_levier_de_latence_se_juge_sur_les_images()
    {
        var parties = new[] { Partie(-3, 90), Partie(-2, 92), Partie(-1, 91), Partie(1, 80), Partie(2, 82), Partie(3, 81) };

        ComparaisonDesParties.Bilan(parties, Reglage, surLaLatence: true)
            .Should().Match<BilanDuChangement>(b => b.Verdict == VerdictEnJeu.Pire && !b.SurLaLatence);
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
