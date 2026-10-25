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

    // Un autre changement fait après lui : les parties d'après le suivant portent l'effet de l'autre. Sans borne, il était
    // retiré et la perte lui était attribuée.
    [Fact]
    public void Les_parties_d_apres_le_changement_suivant_ne_jugent_pas_celui_ci()
    {
        var parties = new[] { Partie(-3, 90), Partie(-2, 92), Partie(-1, 91), Partie(1, 85),
            Partie(5, 80), Partie(6, 82), Partie(7, 81) };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.Pire, "témoin : sans borne, les parties 5 à 7 pèsent");
        ComparaisonDesParties.Juger(parties, Reglage, "cod", jusqua: Reglage.AddDays(4))
            .Should().Be(VerdictEnJeu.PasEncoreJuge, "une seule partie entre les deux changements : pas encore jugé, jamais pire");
    }

    // La borne d'une mesure est sa pire paire, plus bas d'après contre plus haut d'avant, pour une perte comme pour un gain :
    // la valeur la plus basse que le rapport a prise. Pour une perte, la paire la plus proche de zéro (-2,0 % ici) ne
    // borne rien par en dessous ; « au moins » compose ces bornes.
    [Fact]
    public void La_borne_d_une_perte_est_sa_pire_paire()
    {
        var parties = new[] { Partie(-3, 100), Partie(-2, 102), Partie(-1, 104), Partie(1, 90), Partie(2, 92), Partie(3, 98) };

        var bilan = ComparaisonDesParties.Bilan(parties, Reglage);

        bilan.Verdict.Should().Be(VerdictEnJeu.Pire);
        bilan.EcartBornesPercent.Should().BeApproximately((90.0 / 104 - 1) * 100, 1e-9);
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

    // Après, autant de parties de chaque estimateur : celui de la partie la plus récente fait foi, pas le premier dans l'ordre
    // de ses lettres (« PresentMon|1 » avant « PresentMon|0 »), qui rendait ici « pas comparable ».
    [Fact]
    public void A_egalite_l_estimateur_de_la_partie_la_plus_recente_fait_foi()
    {
        static PartieRelevee Toutes(PartieRelevee p) => p with { Low1SurToutesLesImages = true };
        var parties = new[] { Partie(-3, 80), Partie(-2, 81), Partie(-1, 82) }
            .Concat(new[] { Partie(1, 70), Partie(2, 71), Partie(3, 72) }.Select(Toutes))
            .Concat([Partie(4, 90), Partie(5, 91), Partie(6, 92)]).ToList();

        ComparaisonDesParties.Bilan(parties, Reglage).Verdict.Should().Be(VerdictEnJeu.Mieux);
    }

    // Toutes les images des deux côtés, mais avant, les pauses de présentation (alt-tab, chargement) entraient dans le 1 % low :
    // une partie écrite ensuite le perd, et le « mieux » viendrait du calcul, pas du réglage.
    [Fact]
    public void Un_1_pour_cent_low_avec_les_pauses_ne_se_compare_pas_a_un_sans()
    {
        static PartieRelevee Ancien(PartieRelevee p) => p with { Low1SurToutesLesImages = true };
        static PartieRelevee Nouveau(PartieRelevee p) => p with { Low1SurToutesLesImages = true, Low1SansPauses = true };
        var parties = new[] { Ancien(Partie(-3, 80)), Ancien(Partie(-2, 82)), Ancien(Partie(-1, 81)),
            Nouveau(Partie(1, 90)), Nouveau(Partie(2, 92)), Nouveau(Partie(3, 91)) };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.PasComparable);
    }

    // Le repli DXGI (sans hygiène, sans exposition) ne compte pas comme PresentMon : l'écart dirait l'instrument.
    [Fact]
    public void Deux_instruments_ne_se_comparent_pas()
    {
        static PartieRelevee Pm(PartieRelevee p) => p with { Low1SurToutesLesImages = true, Low1SansPauses = true };
        var parties = new[] { Pm(Partie(-3, 80)), Pm(Partie(-2, 82)), Pm(Partie(-1, 81)),
            Pm(Partie(1, 90)) with { Instrument = "DXGI" }, Pm(Partie(2, 92)) with { Instrument = "DXGI" }, Pm(Partie(3, 91)) with { Instrument = "DXGI" } };

        ComparaisonDesParties.Juger(parties, Reglage, "cod").Should().Be(VerdictEnJeu.PasComparable);
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
