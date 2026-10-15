using DrDxt.Core;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// 100 °C en partie, 117 W en médiane, PL 253/253 W au BIOS (relevés sur la machine de référence), refroidisseur sain : le
// docteur relie la chaleur relevée et la limite lue, et donne le plafond qui garde le jeu tel quel.
public sealed class ChaleurEnPartieTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 10, 23, 0, 0, TimeSpan.FromHours(2));

    private static ChaleurEnPartie.PartieLue Partie(double? celsius, double? watts, int ilYAJours = 0, string jeu = "cod.exe")
        => new(jeu, Maintenant.AddDays(-ilYAJours), celsius, watts, 63);

    [Fact]
    public void Le_plafond_garde_la_mediane_avec_une_marge_arrondie_jamais_sous_125()
    {
        ChaleurEnPartie.Plafond(117.6).Should().Be(160);
        ChaleurEnPartie.Plafond(91).Should().Be(130);
        ChaleurEnPartie.Plafond(60).Should().Be(125);
    }

    [Fact]
    public void A_100_degres_avec_une_limite_ouverte_le_plafond_est_conseille_avec_le_guide()
    {
        var c = ChaleurEnPartie.Juger([Partie(100, 117.6), Partie(72, 95, 1)], new LimiteDePuissanceLue(253, 253, 65, false))
            .Should().ContainSingle().Subject;

        c.Domaine.Should().Be("CPU");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.ValeurCourante.Should().Be("jusqu'à 100 °C");
        c.Tout().Should().Contain("100 °C au plus sur « cod.exe »").And.Contain("118 W en médiane").And.Contain("PL1 253 W, PL2 253 W")
            .And.Contain("autour de 160 W").And.Contain("5.3").And.Contain("1 % low");
    }

    [Fact]
    public void Limite_deja_tenue_ou_non_lue_c_est_le_refroidissement()
    {
        var tenue = ChaleurEnPartie.Juger([Partie(98, 117)], new LimiteDePuissanceLue(125, 160, 65, false)).Single();
        tenue.Tout().Should().Contain("déjà tenue").And.Contain("refroidissement").And.NotContain("autour de");

        var nonLue = ChaleurEnPartie.Juger([Partie(98, 117)], null).Single();
        // Limite non lue (AMD, Intel sans PawnIO) : on ne prétend pas qu'il n'y en a pas, et on ne
        // promet pas un levier qui demande de la lire.
        nonLue.Tout().Should().Contain("limite de puissance n'a pas été lue").And.Contain("autour de 160 W")
            .And.NotContain("sans limite de puissance qui tienne").And.NotContain("Optimiser");

        var sansWatts = ChaleurEnPartie.Juger([Partie(98, null)], new LimiteDePuissanceLue(253, 253, 65, false)).Single();
        sansWatts.Tout().Should().Contain("refroidissement").And.NotContain("autour de");
    }

    [Fact]
    public void Sous_le_seuil_une_information_sans_temperature_rien()
    {
        var c = ChaleurEnPartie.Juger([Partie(88, 110), Partie(70, 90, 2)], new LimiteDePuissanceLue(253, 253, 65, false)).Single();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Aucune);
        c.ValeurCourante.Should().Be("jusqu'à 88 °C");

        ChaleurEnPartie.Juger([Partie(null, 110)], null).Should().BeEmpty();
        ChaleurEnPartie.Juger([], null).Should().BeEmpty();
    }

    // Les trois parties « à 100 °C » relues seconde par seconde.
    private static ChaleurEnPartie.PartieLue Freinee(double celsius, FreinageCpu f) => Partie(celsius, 110) with { Freinage = f };

    [Fact]
    public void La_limite_de_courant_se_dit_avant_la_chaleur_meme_sous_le_seuil()
    {
        // Partie relevée le 12/10 : 966 s, courant 544 s, chaleur 41 s, cœur le plus chaud 88 °C en moyenne, cœur 0 73 °C.
        var c = ChaleurEnPartie.Juger([Freinee(93, new FreinageCpu(966, 41, 0, 0, 544, 88, 100, 73))],
            new LimiteDePuissanceLue(160, 160, 65, false)).Single();

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Be("limite de courant 56 % du temps");
        c.Tout().Should().Contain("limite de courant qui le bride").And.Contain("CPU Current Limit").And.Contain("étape 7")
            .And.Contain("cœur 0 à 73 °C").And.Contain("À confirmer : 1 partie(s) de ce jeu sur 3").And.NotContain("autour de")
            .And.NotContain("La chaleur le freine aussi", "41 s sur 966 = 4 %, sous le seuil de chaleur négligeable");
    }

    // Sur un Windows anglais, la même partie se dit en anglais (pourcentages sans espace, date mois/jour).
    [Fact]
    public void La_limite_de_courant_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var c = ChaleurEnPartie.Juger([Freinee(93, new FreinageCpu(966, 41, 0, 0, 544, 88, 100, 73))],
            new LimiteDePuissanceLue(160, 160, 65, false)).Single();

        c.ValeurCourante.Should().Be("current limit 56% of the time");
        c.Tout().Should().StartWith("On your last recorded game (\"cod.exe\", 10/10), the processor was throttled 4% of the time by heat")
            .And.Contain("Its current limit is what holds it back.").And.Contain("core 0 at 73 °C")
            .And.Contain("To be confirmed: 1 game(s) of this title out of 3");
    }

    [Fact]
    public void Des_pics_a_100_degres_sans_freinage_de_chaleur_ne_sont_qu_une_information()
    {
        var c = ChaleurEnPartie.Juger([Freinee(100, new FreinageCpu(600, 12, 0, 30, 0, 85, 100, 70))],
            new LimiteDePuissanceLue(253, 253, 65, false)).Single();

        c.Statut.Should().Be(RepairStatus.Observed);
        c.Tout().Should().Contain("2 % du temps par la chaleur").And.Contain("presque pas freiné").And.Contain("étape 6");
    }

    [Fact]
    public void Freine_par_la_chaleur_le_plafond_reste_conseille_avec_la_lecture_des_secondes()
    {
        var parties = Enumerable.Range(0, 3).Select(j => Freinee(100, new FreinageCpu(600, 120, 0, 0, 0, 92, 100, 90)) with { Fin = Maintenant.AddDays(-j) }).ToList();
        var c = ChaleurEnPartie.Juger(parties, new LimiteDePuissanceLue(253, 253, 65, false)).Single();

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Tout().Should().Contain("20 % du temps par la chaleur").And.Contain("autour de 150 W")
            .And.NotContain("un ou deux cœurs", "le cœur 0 suit le plus chaud").And.NotContain("À confirmer");
    }

    // La valeur lue au BIOS fait le conseil (160 A à la main, puis Auto avec un Lite Load bas).
    [Fact]
    public void Le_conseil_sur_le_courant_nomme_ce_que_le_BIOS_dit()
    {
        var parties = new[] { Freinee(93, new FreinageCpu(966, 41, 0, 0, 544, 88, 100, 73)) };
        var demarrage = Maintenant.AddHours(-2);
        ChaleurEnPartie.BiosLu Lu(params BiosSettingReading[] r) => new(Maintenant.AddHours(-1), r);
        ChaleurEnPartie.Juger(parties, null, Lu(new BiosSettingReading("CPU Current Limit", 160, "160 A", false)), demarrage).Single()
            .Tout().Should().Contain("à la main à 160 A").And.Contain("en Auto");
        ChaleurEnPartie.Juger(parties, null, Lu(new BiosSettingReading("CPU Current Limit", 0, "Auto", true),
                new BiosSettingReading("CPU Lite Load", 80, "Mode 9", false)), demarrage).Single()
            .Tout().Should().Contain("déjà en Auto").And.Contain("en Mode 9");
        // BIOS non lu (autre carte que la MSI qualifiée), le conseil ne parle pas le vocabulaire MSI
        // comme s'il était universel, et dit le cas du portable.
        ChaleurEnPartie.Juger(parties, null).Single().Tout().Should().Contain("le nom change d'une carte à l'autre")
            .And.Contain("chez MSI").And.Contain("portable");
        // Une partie jouée avant ce démarrage ne se juge pas avec le BIOS d'après (il a pu changer).
        ChaleurEnPartie.Juger(parties, null, Lu(new BiosSettingReading("CPU Current Limit", 160, "160 A", false)), Maintenant.AddMinutes(1))
            .Single().Tout().Should().NotContain("160 A");
    }

    [Fact]
    public void Un_releve_du_BIOS_d_avant_ce_demarrage_ou_abime_ne_se_lit_pas()
    {
        var f = Path.Combine(Path.GetTempPath(), "drdxt-bios-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var lu = new ChaleurEnPartie.BiosLu(Maintenant, [new BiosSettingReading("CPU Current Limit", 160, "160 A", false)]);
            File.WriteAllText(f, System.Text.Json.JsonSerializer.Serialize(lu));
            ChaleurEnPartie.LireBios(f, Maintenant.AddHours(-1))!.Reglages.Should().ContainSingle();
            ChaleurEnPartie.LireBios(f, Maintenant.AddHours(1)).Should().BeNull("relevé d'avant ce démarrage");
            File.WriteAllText(f, "[{\"Setting\":\"x\"}]");
            ChaleurEnPartie.LireBios(f, Maintenant.AddHours(-1)).Should().BeNull("ancienne forme ou abîmé");
            ChaleurEnPartie.LireBios(f + ".absent", Maintenant).Should().BeNull();
        }
        finally { File.Delete(f); }
    }

    // Le plafond que la dernière partie demande, seulement si elle a été freinée par la chaleur.
    [Fact]
    public void Le_plafond_thermique_vient_de_la_derniere_partie_freinee_par_la_chaleur()
    {
        var chaude = Freinee(100, new FreinageCpu(600, 120, 0, 0, 0, 92, 100, 90)) with { Fin = Maintenant };
        ChaleurEnPartie.PlafondThermique([chaude]).Should().Be(150, "110 W médians × 1,35, à la dizaine");
        ChaleurEnPartie.PlafondThermique([chaude with { WattsMedian = null }]).Should().BeNull();
        var courant = Freinee(93, new FreinageCpu(966, 41, 0, 0, 544, 88, 100, 73)) with { Fin = Maintenant.AddHours(1) };
        ChaleurEnPartie.PlafondThermique([chaude, courant]).Should().BeNull("la dernière partie est bridée par le courant");
        ChaleurEnPartie.PlafondThermique([Partie(100, 110)]).Should().BeNull("sans secondes relevées, rien n'est posé");
        // Une partie jouée sous le plafond, qui n'a plus chauffé, ne l'efface pas.
        var fraiche = Freinee(80, new FreinageCpu(600, 3, 0, 0, 0, 75, 85, 70)) with { Fin = Maintenant.AddHours(2) };
        ChaleurEnPartie.PlafondThermique([chaude, fraiche]).Should().Be(150);
    }

    // Un i5 non-K tenu à 65 W par sa carte, 90 % du temps à sa limite de puissance, n'est pas
    // « presque pas freiné » (invisible sur la machine de référence, PL1 à 253 W).
    [Fact]
    public void Tenu_par_sa_limite_de_puissance_la_plupart_du_temps_n_est_pas_dit_libre()
    {
        var f = new FreinageCpu(900, 0, 0, 810, 0, 70, 78, 65);
        ChaleurEnPartie.RaisonDe(f).Should().Be(RaisonDuFreinage.Puissance);

        var c = ChaleurEnPartie.Juger([Freinee(78, f)], new LimiteDePuissanceLue(65, 65, 65, false)).Single();
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Be("limite de puissance 90 % du temps");
        c.Tout().Should().NotContain("presque pas freiné").And.Contain("guide 5.3");
    }

    // PROCHOT (un signal de la carte mère, VRM ou alimentation) qui freine, sur un portable ou une
    // carte d'entrée de gamme, se dit pour ce qu'il est.
    [Fact]
    public void Un_processeur_freine_par_PROCHOT_le_dit()
    {
        var f = new FreinageCpu(900, 10, 200, 0, 0, 80, 90, 70);
        ChaleurEnPartie.RaisonDe(f).Should().Be(RaisonDuFreinage.Prochot);
        ChaleurEnPartie.Juger([Freinee(90, f)], null).Single().Tout().Should().Contain("PROCHOT").And.Contain("carte mère");
    }

    // Un Ryzen 7000 vit à 95 °C par conception, un 5800X3D se freine vers 90 °C : hors Intel et
    // sans secondes de freinage relevées, la température seule ne juge rien.
    [Fact]
    public void Hors_Intel_sans_freinage_releve_la_temperature_seule_ne_juge_pas()
    {
        var c = ChaleurEnPartie.Juger([Partie(95, 120)], null, intel: false).Single();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Tout().Should().Contain("ne dit pas s'il se freine").And.NotContain("à 100 °C le processeur se freine");

        ChaleurEnPartie.Juger([Partie(95, 120)], null).Single().Statut.Should().Be(RepairStatus.Suspicious, "Intel : 95 °C reste à regarder");
    }

    // Bridé par le courant, puis réglé ; la partie suivante, propre, fait foi.
    [Fact]
    public void Un_reglage_corrige_se_voit_des_la_partie_suivante()
    {
        var bridee = Freinee(93, new FreinageCpu(966, 41, 0, 0, 544, 88, 100, 73)) with { Fin = Maintenant.AddDays(-1) };
        var propre = Freinee(90, new FreinageCpu(900, 9, 0, 0, 0, 80, 92, 70)) with { Fin = Maintenant };

        var c = ChaleurEnPartie.Juger([bridee, propre], new LimiteDePuissanceLue(160, 160, 65, false)).Single();

        c.Statut.Should().Be(RepairStatus.Observed);
        c.Tout().Should().NotContain("limite de courant qui le bride");
    }

    [Fact]
    public void Seules_les_parties_du_meme_jeu_qui_disent_pareil_confirment()
    {
        var courant = new FreinageCpu(900, 0, 0, 0, 500, 85, 95, 70);
        var parties = new[]
        {
            Freinee(93, courant) with { Fin = Maintenant },
            Freinee(93, courant) with { Fin = Maintenant.AddHours(-1), Jeu = "autre.exe" },
            Freinee(93, courant) with { Fin = Maintenant.AddHours(-2) },
        };
        ChaleurEnPartie.Juger(parties, null).Single().Tout().Should().Contain("2 partie(s) de ce jeu sur 3");
        ChaleurEnPartie.Juger([.. parties, Freinee(93, courant) with { Fin = Maintenant.AddHours(-3) }], null)
            .Single().Tout().Should().NotContain("À confirmer");
    }

    // Le relevé écrit PartieRelevee, le docteur lit PartieLue ; seuls les noms des propriétés les lient.
    [Fact]
    public void Le_freinage_ecrit_par_le_releve_se_relit_par_le_docteur()
    {
        var f = Path.Combine(Path.GetTempPath(), "drdxt-freinage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var freinage = new FreinageCpu(966, 41, 0, 0, 544, 88, 100, 73);
            var ecrite = new DrDxt.Application.Parties.PartieRelevee("cod.exe", Maintenant.AddMinutes(-16), Maintenant, 16, 155, 25,
                BottleneckState.CpuBoundOrAppBound, WattsMedian: 100, CelsiusMax: 100) { Freinage = freinage };
            File.WriteAllText(f, System.Text.Json.JsonSerializer.Serialize(new[] { ecrite }));

            ChaleurEnPartie.Lire(f, Maintenant).Single().Freinage.Should().Be(freinage);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Moins_d_une_minute_de_secondes_ne_juge_pas_le_freinage()
    {
        var c = ChaleurEnPartie.Juger([Freinee(100, new FreinageCpu(30, 0, 0, 0, 30, 90, 100, 70))],
            new LimiteDePuissanceLue(253, 253, 65, false)).Single();
        c.Tout().Should().Contain("se freine lui-même").And.NotContain("limite de courant qui le bride");
    }

    [Fact]
    public void Le_fichier_des_parties_se_lit_deux_semaines_cinq_au_plus_et_jamais_d_erreur()
    {
        var f = Path.Combine(Path.GetTempPath(), "drdxt-chaleur-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(f, """
                [{"Jeu":"cod.exe","Debut":"2026-10-10T22:37:18+02:00","Fin":"2026-10-10T22:54:28+02:00","Captures":6,"FpsMedian":164,"Low1Median":63,"Goulot":5,"WattsMedian":117.6,"CelsiusMax":100,"Autre":1},
                 {"Jeu":"cod.exe","Debut":"2026-09-01T22:00:00+02:00","Fin":"2026-09-01T23:00:00+02:00","Captures":6,"FpsMedian":150,"Low1Median":90,"Goulot":5,"WattsMedian":100,"CelsiusMax":80},
                 {"Jeu":"a.exe","Debut":"2026-10-09T22:00:00+02:00","Fin":"2026-10-09T23:00:00+02:00","Captures":1,"FpsMedian":1,"Low1Median":1,"Goulot":0},
                 {"Jeu":"b.exe","Debut":"2026-10-08T22:00:00+02:00","Fin":"2026-10-08T23:00:00+02:00","Captures":1,"FpsMedian":1,"Low1Median":1,"Goulot":0},
                 {"Jeu":"c.exe","Debut":"2026-10-07T22:00:00+02:00","Fin":"2026-10-07T23:00:00+02:00","Captures":1,"FpsMedian":1,"Low1Median":1,"Goulot":0},
                 {"Jeu":"d.exe","Debut":"2026-10-06T22:00:00+02:00","Fin":"2026-10-06T23:00:00+02:00","Captures":1,"FpsMedian":1,"Low1Median":1,"Goulot":0},
                 {"Jeu":"e.exe","Debut":"2026-10-05T22:00:00+02:00","Fin":"2026-10-05T23:00:00+02:00","Captures":1,"FpsMedian":1,"Low1Median":1,"Goulot":0}]
                """);
            var lues = ChaleurEnPartie.Lire(f, Maintenant);
            lues.Should().HaveCount(5, "cinq au plus, et celle de septembre est hors fenêtre");
            lues[0].Should().Be(new ChaleurEnPartie.PartieLue("cod.exe", DateTimeOffset.Parse("2026-10-10T22:54:28+02:00"), 100, 117.6, 63));
            ChaleurEnPartie.MaxC(lues).Should().Be(100);

            File.WriteAllText(f, "{pas du json");
            ChaleurEnPartie.Lire(f, Maintenant).Should().BeEmpty();
            ChaleurEnPartie.Lire(f + ".absent", Maintenant).Should().BeEmpty();
        }
        finally { File.Delete(f); }
    }

    // Freiné par la chaleur, le
    // docteur juge d'abord le ventilateur, le geste le moins cher : arrêté, courbe trop douce, ou déjà au bout.
    [Fact]
    public void Freine_par_la_chaleur_le_docteur_juge_d_abord_le_ventilateur_du_processeur()
    {
        var limite = new LimiteDePuissanceLue(253, 253, 65, false);
        FreinageCpu Avec(double pwm, double trMin) => new(600, 180, 0, 0, 0, 92, 100, 75, SecondesVentiloChaud: 180,
            VentiloPwmChaud: pwm, VentiloTrMinChaud: trMin);

        var doux = ChaleurEnPartie.Juger([Freinee(100, Avec(55, 1265))], limite).Single().Tout();
        doux.Should().Contain("55 %").And.Contain("1265 tr/min").And.Contain("trop douce").And.Contain("100 %");
        doux.IndexOf("trop douce", StringComparison.Ordinal).Should().BeLessThan(doux.IndexOf("Il tirait", StringComparison.Ordinal),
            "le geste gratuit avant le plafond");

        ChaleurEnPartie.Juger([Freinee(100, Avec(98, 2100))], limite).Single().Tout()
            .Should().Contain("98 %").And.Contain("au bout").And.NotContain("trop douce");
        ChaleurEnPartie.Juger([Freinee(100, Avec(60, 0))], limite).Single().Tout()
            .Should().Contain("ne tournait pas").And.Contain("CPU_FAN");
        ChaleurEnPartie.Juger([Freinee(100, new FreinageCpu(600, 180, 0, 0, 0, 92, 100, 75))], limite).Single().Tout()
            .Should().NotContain("tr/min", "non lu : rien n'est dit, rien n'est deviné");
    }

    // Le geste (plafond, courant, ventilateur) se lit à part du diagnostic.
    [Fact]
    public void Le_geste_se_lit_a_part_du_diagnostic()
    {
        var limite = new LimiteDePuissanceLue(253, 253, 65, false);
        var c = ChaleurEnPartie.Juger([Freinee(100, new FreinageCpu(600, 180, 0, 0, 0, 92, 100, 75))], limite).Single();
        c.Remede.Should().NotBeNullOrWhiteSpace();
        c.Explication.Should().NotContain(c.Remede!);
    }
}

internal static class ConstatChaleur
{
    public static string Tout(this RepairFinding f) => f.Explication + " " + f.Remede;
}
