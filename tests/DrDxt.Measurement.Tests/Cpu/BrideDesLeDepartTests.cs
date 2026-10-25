using DrDxt.Measurement.Cpu;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Cpu;

/// <summary>
/// Référence mesurée le 30/09 : rafale 1 391, soutenu 1 533 Mops (aucune baisse), 66 W à ±3,9 %,
/// « Intel Default Settings ». « Rien à faire » serait faux : le processeur ne baisse pas parce qu'il est
/// retenu dès la première seconde. Avec la fréquence d'un seul cœur et celle de tous, le juge le voit.
/// </summary>
public class BrideDesLeDepartTests
{
    private static readonly DateTimeOffset Prise = new(2026, 9, 30, 2, 30, 0, TimeSpan.FromHours(2));

    private static CpuBaseline Reference(double? unCoeur, double? tous, double ecartPuissance = 3.9) =>
        new("m", Prise, 8, RafaleMops: 1391.3, SoutenuMops: 1533.0,
            BruitPercent: 1.84, PuissanceSoutenueW: 66.4, PuissanceEcartPercent: ecartPuissance,
            FrequenceUnCoeurMhz: unCoeur, FrequenceSoutenueMhz: tous);

    [Fact]
    public void Retenu_des_le_depart_a_puissance_plate_la_limite_du_BIOS_est_nommee()
    {
        var v = CpuBaselineService.Juger(Reference(5292, 3698));

        v.BrideDesLeDepart.Should().BeTrue();
        v.LimiteDePuissance.Should().BeTrue("l'App nomme alors le réglage du BIOS");
        v.Demontrable.Should().BeFalse("il n'y a pas de baisse : « démontrable » garde son sens");
        v.Lisible.Should().Contain("à froid, il n'allait déjà pas plus vite").And.Contain("3698 MHz contre 5292 MHz")
            .And.Contain("66 W stables").And.Contain("ou par le fabricant sur un portable").And.Contain($"Mesuré le {Prise.ToLocalTime():dd/MM/yyyy}")
            .And.NotContain("première seconde", "watts et fréquences se relèvent à partir de la 20e seconde");
    }

    [Theory]
    [InlineData(5600.0, 5400.0)]   // écart naturel du turbo d'un cœur à celui de tous (i7-14700)
    [InlineData(4300.0, 3700.0)]   // 86 % : juste au-dessus du seuil
    public void Un_ecart_de_turbo_ordinaire_ne_designe_pas_le_BIOS(double unCoeur, double tous)
    {
        var v = CpuBaselineService.Juger(Reference(unCoeur, tous));
        v.BrideDesLeDepart.Should().BeFalse();
        v.Lisible.Should().StartWith("Ton processeur garde sa puissance");
    }

    [Fact]
    public void Sans_frequence_ou_a_puissance_qui_varie_rien_n_est_affirme()
    {
        CpuBaselineService.Juger(Reference(null, null)).BrideDesLeDepart.Should().BeFalse("référence d'avant le relevé");
        CpuBaselineService.Juger(Reference(5292, null)).BrideDesLeDepart.Should().BeFalse();
        CpuBaselineService.Juger(Reference(5292, 3698, ecartPuissance: 15)).BrideDesLeDepart
            .Should().BeFalse("puissance qui varie : chaleur ou limite, on ne sait pas");
    }
}
