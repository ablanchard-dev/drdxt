using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

public class AjustementDuPlafondTests
{
    private const long Mbit = 1_000_000;

    private static List<EchantillonEnvoi> Fenetre(double envoiMbps, double pingChargeMs, int nCharge = 40)
        => Enumerable.Range(0, 40).Select(i => new EchantillonEnvoi(2, 8 + (i % 3)))
            .Concat(Enumerable.Range(0, nCharge).Select(i => new EchantillonEnvoi(envoiMbps, pingChargeMs + (i % 3))))
            .ToList();

    [Fact]
    public void Le_ping_monte_encore_le_plafond_descend_de_20_pour_cent()
        => AjustementDuPlafond.Suivant(400 * Mbit, 720 * Mbit, Fenetre(400, 30)).Should().Be(320 * Mbit);

    [Fact]
    public void Une_marge_nette_le_plafond_remonte_de_10_pour_cent_sans_depasser_la_capacite_du_scan()
    {
        AjustementDuPlafond.Suivant(300 * Mbit, 720 * Mbit, Fenetre(300, 9)).Should().Be(330 * Mbit);
        AjustementDuPlafond.Suivant(700 * Mbit, 720 * Mbit, Fenetre(700, 9)).Should().Be(720 * Mbit);
    }

    [Fact]
    public void Sans_preuve_on_ne_bouge_pas()
    {
        AjustementDuPlafond.Suivant(400 * Mbit, 720 * Mbit, Fenetre(400, 40, nCharge: 5))
            .Should().Be(400 * Mbit, "cinq echantillons en charge ne prouvent rien");
        AjustementDuPlafond.Suivant(400 * Mbit, 720 * Mbit, Fenetre(400, 18))
            .Should().Be(400 * Mbit, "une hausse de 10 ms est la zone grise");
    }

    [Fact]
    public void Jamais_sous_le_plancher_du_helper()
        => AjustementDuPlafond.Suivant(1 * Mbit, 720 * Mbit, Fenetre(1, 60)).Should().Be(AjustementDuPlafond.PlancherBps);

    [Theory]
    [InlineData(700)]
    [InlineData(360)]
    [InlineData(100)]
    public void En_boucle_contre_une_box_qui_sature_a_300_le_plafond_se_pose_dans_la_bande_utile(int departMbps)
    {
        // Box deterministe : au-dessus de 300 Mbit/s d'envoi, sa file se remplit, +0,2 ms par Mbit/s.
        static double Ping(double envoiMbps) => 8 + Math.Max(0, envoiMbps - 300) * 0.2;

        long plafond = departMbps * Mbit;
        for (var i = 0; i < 30; i++)
        {
            var envoi = plafond / (double)Mbit;
            plafond = AjustementDuPlafond.Suivant(plafond, 720 * Mbit, Fenetre(envoi, Ping(envoi)));
            plafond.Should().BeInRange(AjustementDuPlafond.PlancherBps, 720 * Mbit);
        }

        // Bande utile : hausse sous le seuil (plafond < 375) mais assez ouverte pour ne pas remonter (>= 337,5).
        (plafond / (double)Mbit).Should().BeInRange(337.5, 375);
    }
}
