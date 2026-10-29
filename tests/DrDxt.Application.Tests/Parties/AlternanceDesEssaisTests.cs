using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Un levier jugé sur des parties A/B en blocs A-B-B-A, sans geste à chaque partie, après un test à blanc A contre A.
public sealed class AlternanceDesEssaisTests
{
    private const string Levier = "latency.hagsOn";

    private static PartieRelevee P(int jour, double low1, bool? pose, string jeu = "cod.exe") =>
        new(jeu, new DateTimeOffset(2026, 10, jour, 20, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, jour, 20, 30, 0, TimeSpan.Zero),
            4, 130, low1, BottleneckState.CpuBoundOrAppBound,
            Essai: pose is { } b ? AlternanceDesEssais.Marque(Levier, b) : null);

    // Les parties dans l'ordre A-B-B-A : avec, sans, sans, avec, avec, sans, sans, avec, avec, sans, sans, avec.
    private static PartieRelevee[] Abba(double[] avec, double[] sans)
    {
        var parties = new List<PartieRelevee>();
        int a = 0, s = 0;
        for (var rang = 0; a < avec.Length || s < sans.Length; rang++)
        {
            var pose = AlternanceDesEssais.PoseAuRang(rang);
            if (pose && a < avec.Length) parties.Add(P(rang + 1, avec[a++], true));
            else if (!pose && s < sans.Length) parties.Add(P(rang + 1, sans[s++], false));
        }
        return [.. parties];
    }

    // Le jeu choisi sous Optimiser est celui qu'on juge, même moins joué : JeuB a ses douze parties, JeuA deux seulement.
    [Fact]
    public void Le_jeu_choisi_est_juge_meme_moins_joue_que_les_autres()
    {
        var jeuB = Abba([95, 96, 97, 95, 96, 97], SansCalme).Select(p => p with { Jeu = "JeuB.exe" }).ToList();
        var jeuA = new[] { P(20, 95, true, "JeuA.exe"), P(21, 80, false, "JeuA.exe") };
        var parties = jeuB.Concat(jeuA).ToList();

        AlternanceDesEssais.Juger(parties, Levier).Should().Be(VerdictEnJeu.Mieux, "témoin : sans choix, JeuB, le plus joué, est jugé");
        AlternanceDesEssais.Juger(parties, Levier, jeuChoisi: "JeuA.exe").Should().Be(VerdictEnJeu.PasEncoreJuge,
            "JeuA n'a pas encore assez de parties");
        AlternanceDesEssais.Suivante(parties, Levier, poseALOrigine: false, jeuChoisi: "JeuA.exe").Pose
            .Should().BeFalse("deux parties de JeuA jouées : rang 2, « sans » (les douze de JeuB donneraient « avec »)");
    }

    // Six parties sans le levier dont les deux moitiés alternées se chevauchent : le test à blanc passe.
    private static readonly double[] SansCalme = [80, 81, 79, 82, 80.5, 78];

    // Un levier de latence (famille « latence » du catalogue) se juge sur la latence d'affichage,
    // plus bas = mieux ; sans latence sur toutes les parties, il retombe sur les 1 % bas.
    private const string LevierLatence = "latency.render_queue_cap";

    private static PartieRelevee[] Latences(double[] avec, double[] sans) => Abba(avec, sans)
        .Select((p, i) => p with
        {
            Essai = AlternanceDesEssais.Marque(LevierLatence, p.Essai!.EndsWith(":on", StringComparison.Ordinal)),
            LatenceAffichageMedianMs = p.Low1Median,
            Low1Median = 90,
        }).ToArray();

    [Fact]
    public void Un_levier_de_latence_se_juge_sur_la_latence_d_affichage_plus_bas_est_mieux()
    {
        AlternanceDesEssais.JugeSurLaLatence(LevierLatence).Should().BeTrue();
        AlternanceDesEssais.JugeSurLaLatence("gpu.power_limit").Should().BeFalse("la limite de puissance de la carte se juge sur les images");
        double[] sans = [25, 26, 27, 25.5, 26.5, 24.5];
        AlternanceDesEssais.Juger(Latences([20, 21, 19], sans), LevierLatence).Should().Be(VerdictEnJeu.Mieux, "avec : 19-21 ms, sans : 24,5-27 ms");
        AlternanceDesEssais.Juger(Latences([30, 31, 29], sans), LevierLatence).Should().Be(VerdictEnJeu.Pire);
    }

    // Une partie sans latence lue ne fait plus juger le levier de latence sur les images : elle ne compte d'aucun côté. Un
    // levier de latence coûte souvent un peu d'images ; jugé sur elles, il était retiré à tort.
    [Fact]
    public void Une_partie_sans_latence_ne_compte_pas_et_le_levier_de_latence_reste_juge_sur_la_latence()
    {
        var parties = Latences([20, 21, 19], [25, 26, 27, 25.5, 26.5, 24.5, 25]);
        parties[^1] = parties[^1] with { LatenceAffichageMedianMs = null };

        AlternanceDesEssais.Juger(parties, LevierLatence).Should().Be(VerdictEnJeu.Mieux, "six parties sans portent la latence, 24,5-27 ms contre 19-21");
        AlternanceDesEssais.JugeSurLesImagesFauteDeLatence(parties, LevierLatence).Should().BeFalse();
    }

    // Jugé sur la latence, l'estimateur du 1 % low ne concerne pas l'essai : ses parties comptent toutes.
    [Fact]
    public void Un_essai_juge_sur_la_latence_ne_filtre_pas_l_estimateur_du_1_pour_cent_low()
    {
        var parties = Latences([20, 21, 19], [25, 26, 27, 25.5, 26.5, 24.5]).Select(p => p with { Low1SurToutesLesImages = true }).ToArray();
        var deuxSans = parties.Where(p => p.Essai!.EndsWith(":off", StringComparison.Ordinal)).Take(2).ToHashSet();
        var melangees = parties.Select(p => deuxSans.Contains(p) ? p with { Low1SurToutesLesImages = false } : p).ToArray();

        AlternanceDesEssais.Juger(melangees, LevierLatence).Should().Be(VerdictEnJeu.Mieux, "les six parties sans portent la latence");
    }

    // L'instrument ne lit la latence sur aucune partie (ce jeu, ce pilote) : les 1 % bas restent la seule mesure, et c'est dit.
    [Fact]
    public void Sans_latence_sur_aucune_partie_le_levier_de_latence_se_juge_sur_les_images_et_le_dit()
    {
        var parties = Latences([20, 21, 19], [25, 26, 27, 25.5, 26.5, 24.5])
            .Select(p => p with { LatenceAffichageMedianMs = null, Low1Median = p.Essai!.EndsWith(":on", StringComparison.Ordinal) ? 95 : 80 })
            .ToArray();

        AlternanceDesEssais.Juger(parties, LevierLatence).Should().Be(VerdictEnJeu.Mieux, "1 % bas : 95 avec, 80 sans");
        AlternanceDesEssais.JugeSurLesImagesFauteDeLatence(parties, LevierLatence).Should().BeTrue();
        AlternanceDesEssais.JugeSurLesImagesFauteDeLatence(Latences([20, 21, 19], [25, 26, 27, 25.5, 26.5, 24.5]), LevierLatence)
            .Should().BeFalse("témoin : la latence est lue");
    }

    // Deux parties « sans » mesurées par la médiane des fenêtres (l'autre estimateur du 1 % low, plus haut en général) ne se
    // comparent pas aux autres : il manque alors des parties sans, l'essai continue.
    [Fact]
    public void Les_parties_d_un_autre_estimateur_ne_comptent_pas()
    {
        var parties = Abba([90, 91, 92], SansCalme).Select(p => p with { Low1SurToutesLesImages = true }).ToArray();
        AlternanceDesEssais.Juger(parties, Levier).Should().Be(VerdictEnJeu.Mieux, "témoin : un seul estimateur");

        var sans = parties.Where(p => p.Essai!.EndsWith(":off", StringComparison.Ordinal)).Take(2).ToHashSet();
        var melangees = parties.Select(p => sans.Contains(p) ? p with { Low1SurToutesLesImages = false } : p).ToArray();
        AlternanceDesEssais.Juger(melangees, Levier).Should().Be(VerdictEnJeu.PasEncoreJuge, "quatre parties sans du même estimateur, il en faut six");
    }

    [Fact]
    public void Trois_parties_avec_nettement_au_dessus_de_six_sans_disent_mieux()
        => AlternanceDesEssais.Juger(Abba([90, 91, 92], SansCalme), Levier).Should().Be(VerdictEnJeu.Mieux);

    [Fact]
    public void Des_parties_qui_se_chevauchent_ne_disent_rien_de_net()
        => AlternanceDesEssais.Juger(Abba([90, 79.5, 92], SansCalme), Levier).Should().Be(VerdictEnJeu.PasDeDifference);

    [Fact]
    public void Sans_six_parties_sans_le_levier_ou_sur_un_autre_jeu_rien_n_est_tranche()
    {
        AlternanceDesEssais.Juger(Abba([90, 91, 92], [80, 81, 79]), Levier)
            .Should().Be(VerdictEnJeu.PasEncoreJuge, "le test à blanc demande deux moitiés de trois parties sans");
        AlternanceDesEssais.Juger(Abba([90, 91, 92], SansCalme).Select(p => p with { Jeu = p.Essai!.EndsWith(":on", StringComparison.Ordinal) ? "a.exe" : "b.exe" }).ToList(), Levier)
            .Should().Be(VerdictEnJeu.PasEncoreJuge, "aucun jeu joué des deux côtés");
        AlternanceDesEssais.Juger([P(1, 90, null), P(2, 80, null), P(3, 91, null)], Levier).Should().Be(VerdictEnJeu.PasEncoreJuge);
    }

    // Un instrument qui voit un écart entre deux groupes au même réglage voit du bruit : quand les parties sans le levier se
    // séparent déjà entre elles, l'essai est non concluant, même avec des parties « avec » nettement au-dessus, et l'état d'origine revient.
    [Fact]
    public void Un_test_a_blanc_qui_voit_un_ecart_rend_l_essai_non_concluant_et_aucun_verdict_ne_suit()
    {
        var parties = Abba([95, 96, 97], [70, 90, 71, 91, 72, 92]);
        AlternanceDesEssais.Juger(parties, Levier).Should().Be(VerdictEnJeu.NonConcluant);
        AlternanceDesEssais.Suivante(parties, Levier, poseALOrigine: false).Should().Be((false, true, VerdictEnJeu.NonConcluant));
        AlternanceDesEssais.Suivante(parties, Levier, poseALOrigine: true).Pose.Should().BeTrue("l'état d'origine revient");
    }

    // Le joueur passe d'un jeu à l'autre pendant l'essai : le rang des blocs se compte sur le jeu jugé, sinon une partie
    // d'un autre jeu décalait l'ordre A-B-B-A de celui-ci. Call of Duty « avec », puis deux Valorant : la prochaine Call of
    // Duty est la deuxième du bloc, « sans ».
    [Fact]
    public void Le_rang_des_blocs_se_compte_sur_le_jeu_juge()
    {
        var parties = new[] { P(1, 90, true), P(2, 80, false, "valorant.exe"), P(3, 80, false, "valorant.exe"), P(4, 91, true) };

        AlternanceDesEssais.Suivante(parties, Levier, poseALOrigine: false).Pose.Should().BeFalse();
    }

    [Fact]
    public void Tant_que_rien_n_est_tranche_les_parties_suivent_des_blocs_abba()
    {
        AlternanceDesEssais.Suivante([], Levier, poseALOrigine: false).Should().Be((true, false, VerdictEnJeu.PasEncoreJuge));
        var faites = new List<PartieRelevee>();
        var ordre = new List<bool>();
        for (var i = 0; i < 8; i++)
        {
            var pose = AlternanceDesEssais.Suivante(faites, Levier, false).Pose;
            ordre.Add(pose);
            faites.Add(P(i + 1, 80, pose));
        }
        ordre.Should().Equal(true, false, false, true, true, false, false, true);
    }

    [Fact]
    public void Tranche_l_alternance_s_arrete_sur_le_meilleur_etat()
    {
        AlternanceDesEssais.Suivante(Abba([90, 91, 92], SansCalme), Levier, false).Should().Be((true, true, VerdictEnJeu.Mieux));
        AlternanceDesEssais.Suivante(Abba([70, 71, 72], SansCalme), Levier, false).Should().Be((false, true, VerdictEnJeu.Pire));
        AlternanceDesEssais.Suivante(Abba([90, 79.5, 92], SansCalme), Levier, poseALOrigine: true).Should().Be((true, true, VerdictEnJeu.PasDeDifference),
            "sans différence, l'état d'origine revient");
    }
}
