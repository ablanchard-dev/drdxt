using DrDxt.Measurement.Network;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

public class BufferbloatTests
{
    private static IEnumerable<EchantillonEnvoi> N(int n, double mbps, double ping)
        => Enumerable.Range(0, n).Select(i => new EchantillonEnvoi(mbps, ping + (i % 3)));

    // En partie, le plus fort envoi vu est souvent le trafic du jeu (moins de 2 Mbit/s) : la moitié tomberait sous le
    // plancher du helper (1 Mbit/s), la carte « Activer » serait refusée, et « ton envoi a saturé à ~1 Mbit/s » ne serait pas
    // prouvé. Rien n'est détecté.
    [Fact]
    public void Un_envoi_trop_faible_pour_porter_un_plafond_ne_se_dit_pas_sature()
    {
        var v = Bufferbloat.Juger(N(40, 0.1, 8).Concat(N(40, 1.5, 40)).ToList());

        v.Detecte.Should().BeFalse();
        v.PlafondConseilleBps.Should().Be(0);
        v.Raison.Should().Be(Docteur.T("BB.SousLePlancher", 1.5));
    }

    [Fact]
    public void Le_profil_mesure_sur_la_machine_de_reference_est_detecte_et_le_plafond_vaut_la_moitie_de_l_envoi_vu()
    {
        // Repos 8-9 ms, envoi sature ~720 Mbit/s -> p90 26 ms.
        var e = N(40, 2, 8).Concat(N(40, 720, 24)).ToList();
        var v = Bufferbloat.Juger(e);
        v.Detecte.Should().BeTrue();
        v.CapaciteMbps.Should().Be(720);
        v.PlafondConseilleBps.Should().Be(360_000_000);
    }

    // Deux regles que des mutations laissaient vertes.
    private static IEnumerable<EchantillonEnvoi> Fixe(int n, double mbps, double ping)
        => Enumerable.Repeat(new EchantillonEnvoi(mbps, ping), n);

    // Le p90 prenait l'indice floor(0,9·n), soit le 19e sur 20 (un p95), quand le reste du
    // produit (FrametimeStatistics.PercentileOrNull) prend le rang le plus proche, ceil(0,9·n) − 1.
    [Fact]
    public void Le_p90_est_le_rang_le_plus_proche_comme_ailleurs_dans_le_produit()
    {
        var repos = Enumerable.Range(1, 20).Select(i => new EchantillonEnvoi(1, i));
        var charge = Enumerable.Repeat(new EchantillonEnvoi(100, 100), 20);

        var v = Bufferbloat.Juger(repos.Concat(charge).ToList());

        v.PingReposP90.Should().Be(18);
    }

    [Fact]
    public void Une_hausse_d_exactement_le_seuil_est_detectee()
    {
        // « d'au moins SeuilMs » : 10 -> 25 ms, soit +15 pile, compte.
        var v = Bufferbloat.Juger(Fixe(40, 2, 10).Concat(Fixe(40, 720, 10 + Bufferbloat.SeuilMs)).ToList());
        v.Detecte.Should().BeTrue();
    }

    [Fact]
    public void Les_envois_moyens_ne_diluent_pas_la_charge()
    {
        // Seuls les envois proches du maximum comptent comme charge. Sinon, 600 envois a mi-debit et au
        // ping calme noieraient les 40 echantillons satures, et le p90 ne verrait plus le vrai defaut.
        var e = Fixe(40, 2, 8).Concat(Fixe(40, 720, 40)).Concat(Fixe(600, 360, 8)).ToList();
        Bufferbloat.Juger(e).Detecte.Should().BeTrue();
    }

    [Fact]
    public void Un_ping_qui_ne_monte_pas_sous_charge_ne_propose_aucun_plafond()
    {
        var v = Bufferbloat.Juger(N(40, 2, 8).Concat(N(40, 720, 10)).ToList());
        v.Detecte.Should().BeFalse();
        v.PlafondConseilleBps.Should().Be(0);
        v.Raison.Should().Contain("rien à corriger");
    }

    [Fact]
    public void Sans_assez_d_echantillons_en_charge_on_ne_conclut_rien()
    {
        // Une seule rafale d'envoi : un p90 sur 5 valeurs est un tirage.
        var v = Bufferbloat.Juger(N(40, 2, 8).Concat(N(5, 720, 60)).ToList());
        v.Detecte.Should().BeFalse();
        v.Raison.Should().Contain("pas assez observé");
    }

    [Fact]
    public void Une_machine_qui_n_envoie_jamais_n_a_rien_a_corriger()
        => Bufferbloat.Juger(N(60, 0, 8).ToList()).Raison.Should().Be("aucun envoi observé");

    [Fact]
    public void Les_pings_perdus_ne_comptent_ni_comme_zero_ni_comme_valeur()
    {
        var e = N(40, 2, 8).Concat(N(40, 720, 30))
            .Concat(Enumerable.Repeat(new EchantillonEnvoi(720, null), 50)).ToList();
        var v = Bufferbloat.Juger(e);
        v.NCharge.Should().Be(40);
        v.Detecte.Should().BeTrue();
    }

    [Fact]
    public void Le_seuil_est_une_hausse_pas_une_valeur_absolue()
    {
        // Un joueur loin du serveur : 40 ms au repos, 50 en charge. +10 ms : sous le seuil.
        Bufferbloat.Juger(N(40, 2, 40).Concat(N(40, 500, 50)).ToList()).Detecte.Should().BeFalse();
    }

    [Fact]
    public void Seule_une_detection_propose_un_plafond_et_rien_ne_s_ecrit_tout_seul()
    {
        var f = TimeSpan.FromSeconds(20);
        var detecte = Bufferbloat.Constat(Bufferbloat.Juger(N(40, 2, 8).Concat(N(40, 720, 30)).ToList()), f);
        detecte.Statut.Should().Be(RepairStatus.Suspicious);
        detecte.Action.Should().Be(RepairAction.Recommandation, "jamais une reparation automatique");
        detecte.Explication.Should().Contain("360 Mbit/s");
        detecte.Explication.Should().Contain("routeur").And.NotContain("SQM");
        detecte.Remede.Should().StartWith("1) ").And.Contain("Protéger ton ping pendant le jeu").And.Contain("360 Mbit/s")
            .And.Contain("SQM", "le plafond ne règle que ce PC ; la maison entière passe par le routeur");

        Bufferbloat.Constat(Bufferbloat.Juger(N(40, 2, 8).ToList()), f).Action.Should().Be(RepairAction.Aucune);
        Bufferbloat.Constat(Bufferbloat.Juger(N(40, 2, 8).Concat(N(5, 720, 60)).ToList()), f)
            .Statut.Should().Be(RepairStatus.Observed, "pas assez vu n'est ni sain ni malade");
    }

    // Sur un Windows anglais, le constat du ping sous envoi se dit en anglais.
    [Fact]
    public void Le_bufferbloat_se_dit_en_anglais()
    {
        using var _ = DrDxt.Core.Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var v = new VerdictBufferbloat(true, 50, 20, 80, 30, 30, 40_000_000, "x");

        var c = Bufferbloat.Constat(v, TimeSpan.FromSeconds(60));
        c.ValeurCourante.Should().Be("+60 ms (p90 20 → 80 ms)");
        c.Explication.Should().StartWith("during 60 s of play, your upload saturated at ~50 Mbit/s");
    }
}
