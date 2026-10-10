using DrDxt.Core;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// 10/10/2026 soir : 100 °C en partie, 117 W en médiane, PL 253/253 W au BIOS depuis le 29/09, refroidisseur sain : le
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
        c.Explication.Should().Contain("100 °C au plus sur « cod.exe »").And.Contain("118 W en médiane").And.Contain("PL1 253 W, PL2 253 W")
            .And.Contain("autour de 160 W").And.Contain("5.3").And.Contain("1 % low");
    }

    [Fact]
    public void Limite_deja_tenue_ou_non_lue_c_est_le_refroidissement()
    {
        var tenue = ChaleurEnPartie.Juger([Partie(98, 117)], new LimiteDePuissanceLue(125, 160, 65, false)).Single();
        tenue.Explication.Should().Contain("déjà tenue").And.Contain("refroidissement").And.NotContain("autour de");

        var nonLue = ChaleurEnPartie.Juger([Partie(98, 117)], null).Single();
        nonLue.Explication.Should().Contain("sans limite de puissance qui tienne").And.Contain("autour de 160 W");

        var sansWatts = ChaleurEnPartie.Juger([Partie(98, null)], new LimiteDePuissanceLue(253, 253, 65, false)).Single();
        sansWatts.Explication.Should().Contain("refroidissement").And.NotContain("autour de");
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
}
