using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Un levier jugé sur des parties A/B posées une sur deux, sans geste à chaque partie.
public sealed class AlternanceDesEssaisTests
{
    private const string Levier = "latency.hagsOn";

    private static PartieRelevee P(int jour, double low1, bool? pose, string jeu = "cod.exe") =>
        new(jeu, new DateTimeOffset(2026, 10, jour, 20, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, jour, 20, 30, 0, TimeSpan.Zero),
            4, 130, low1, BottleneckState.CpuBoundOrAppBound,
            Essai: pose is { } b ? AlternanceDesEssais.Marque(Levier, b) : null);

    // Un levier de latence (famille « latence » du catalogue) se juge sur la latence d'affichage,
    // plus bas = mieux ; sans latence sur toutes les parties, il retombe sur les 1 % bas.
    private const string LevierLatence = "latency.render_queue_cap";

    private static PartieRelevee L(int jour, double latence, bool pose, double low1 = 90) =>
        P(jour, low1, null) with { Essai = AlternanceDesEssais.Marque(LevierLatence, pose), LatenceAffichageMedianMs = latence };

    [Fact]
    public void Un_levier_de_latence_se_juge_sur_la_latence_d_affichage_plus_bas_est_mieux()
    {
        AlternanceDesEssais.JugeSurLaLatence(LevierLatence).Should().BeTrue();
        AlternanceDesEssais.JugeSurLaLatence("gpu.power_limit").Should().BeFalse("la limite de puissance de la carte se juge sur les images");
        AlternanceDesEssais.Juger([L(1, 20, true), L(2, 26, false), L(3, 21, true), L(4, 25, false), L(5, 19, true), L(6, 27, false)], LevierLatence)
            .Should().Be(VerdictEnJeu.Mieux, "avec : 19-21 ms, sans : 25-27 ms");
        AlternanceDesEssais.Juger([L(1, 30, true), L(2, 26, false), L(3, 31, true), L(4, 25, false), L(5, 29, true), L(6, 27, false)], LevierLatence)
            .Should().Be(VerdictEnJeu.Pire);
    }

    [Fact]
    public void Sans_latence_sur_toutes_les_parties_le_levier_de_latence_retombe_sur_les_images()
    {
        var parties = new[] { L(1, 20, true), L(2, 26, false), L(3, 21, true), L(4, 25, false), L(5, 19, true), L(6, 27, false) with { LatenceAffichageMedianMs = null } };
        AlternanceDesEssais.Juger(parties, LevierLatence).Should().Be(VerdictEnJeu.PasDeDifference, "1 % bas identiques des deux côtés");
    }

    [Fact]
    public void Trois_parties_avec_nettement_au_dessus_de_trois_sans_disent_mieux()
        => AlternanceDesEssais.Juger([P(1, 90, true), P(2, 80, false), P(3, 91, true), P(4, 81, false), P(5, 92, true), P(6, 79, false)], Levier)
            .Should().Be(VerdictEnJeu.Mieux);

    [Fact]
    public void Des_parties_qui_se_chevauchent_ne_disent_rien_de_net()
        => AlternanceDesEssais.Juger([P(1, 90, true), P(2, 89, false), P(3, 85, true), P(4, 91, false), P(5, 92, true), P(6, 80, false)], Levier)
            .Should().Be(VerdictEnJeu.PasDeDifference);

    [Fact]
    public void Moins_de_trois_de_chaque_cote_ou_un_autre_jeu_ne_tranche_pas()
    {
        AlternanceDesEssais.Juger([P(1, 90, true), P(2, 80, false), P(3, 91, true), P(4, 81, false)], Levier)
            .Should().Be(VerdictEnJeu.PasEncoreJuge);
        AlternanceDesEssais.Juger([P(1, 90, true, "a.exe"), P(2, 80, false, "b.exe"), P(3, 91, true, "a.exe"),
                P(4, 81, false, "b.exe"), P(5, 92, true, "a.exe"), P(6, 79, false, "b.exe")], Levier)
            .Should().Be(VerdictEnJeu.PasEncoreJuge, "aucun jeu joué des deux côtés");
        AlternanceDesEssais.Juger([P(1, 90, null), P(2, 80, null), P(3, 91, null)], Levier).Should().Be(VerdictEnJeu.PasEncoreJuge);
    }

    [Fact]
    public void Tant_que_rien_n_est_tranche_la_partie_suivante_prend_l_inverse_de_la_derniere()
    {
        AlternanceDesEssais.Suivante([], Levier, poseALOrigine: false).Should().Be((true, false, VerdictEnJeu.PasEncoreJuge));
        AlternanceDesEssais.Suivante([P(1, 90, true)], Levier, false).Pose.Should().BeFalse();
        AlternanceDesEssais.Suivante([P(1, 90, true), P(2, 80, false)], Levier, false).Pose.Should().BeTrue();
    }

    [Fact]
    public void Tranche_l_alternance_s_arrete_sur_le_meilleur_etat()
    {
        var mieux = new[] { P(1, 90, true), P(2, 80, false), P(3, 91, true), P(4, 81, false), P(5, 92, true), P(6, 79, false) };
        AlternanceDesEssais.Suivante(mieux, Levier, false).Should().Be((true, true, VerdictEnJeu.Mieux));
        var pire = new[] { P(1, 70, true), P(2, 80, false), P(3, 71, true), P(4, 81, false), P(5, 72, true), P(6, 79, false) };
        AlternanceDesEssais.Suivante(pire, Levier, false).Should().Be((false, true, VerdictEnJeu.Pire));
        var pareil = new[] { P(1, 90, true), P(2, 89, false), P(3, 85, true), P(4, 91, false), P(5, 92, true), P(6, 80, false) };
        AlternanceDesEssais.Suivante(pareil, Levier, poseALOrigine: true).Should().Be((true, true, VerdictEnJeu.PasDeDifference),
            "sans différence, l'état d'origine revient");
    }
}
