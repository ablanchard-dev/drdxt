using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

public sealed class CarteChaudeEnPartieTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 12, 0, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Les_lectures_freinees_par_la_chaleur_se_comptent_et_se_cumulent()
    {
        static HardwareReading L(ClocksEventReason r) => new(100, 70, 2700, r);
        var m = MecanismeCarte.Depuis(Enumerable.Repeat(L(ClocksEventReason.ThermiqueLogiciel), 10)
            .Concat(Enumerable.Repeat(L(ClocksEventReason.ThermiqueMateriel | ClocksEventReason.PlafondPuissanceLogiciel), 10))
            .Concat(Enumerable.Repeat(L(ClocksEventReason.Aucune), 80)))!;
        m.BridesChaleur.Should().Be(20);
        m.PourcentChaleur.Should().Be(20);
        MecanismeCarte.Cumuler([m, m])!.BridesChaleur.Should().Be(40);

        // Freinée par la chaleur, la carte tire moins ; ces lectures comptent quand même.
        var freinees = Enumerable.Repeat(new HardwareReading(40, 70, 1500, ClocksEventReason.ThermiqueMateriel), 20)
            .Concat(Enumerable.Repeat(new HardwareReading(100, 70, 2700, ClocksEventReason.Aucune), 80));
        MecanismeCarte.Depuis(freinees)!.BridesChaleur.Should().Be(20);
        // 20 lectures chaudes sur 100 lectures actives, c'est 20 %, pas 20 sur les 80 « en charge ».
        MecanismeCarte.Depuis(freinees)!.PourcentChaleur.Should().Be(20);
    }

    [Fact]
    public void Une_carte_freinee_par_la_chaleur_en_partie_est_dite_sinon_rien()
    {
        var chaude = new CarteChaudeEnPartie.PartieLue("cod.exe", T, new MecanismeCarte(600, 0, null, 2700, BridesChaleur: 60), 84);
        var c = CarteChaudeEnPartie.Juger([chaude]).Single();
        c.Domaine.Should().Be("GPU");
        c.ValeurCourante.Should().Be("freinée 10 % du temps");
        c.Explication.Should().Contain("jusqu'à 84 °C").And.Contain("guide 5.21");

        CarteChaudeEnPartie.Juger([chaude with { MecanismeCarte = new MecanismeCarte(600, 0, null, 2700, BridesChaleur: 6) }]).Should().BeEmpty();
        CarteChaudeEnPartie.Juger([chaude with { MecanismeCarte = new MecanismeCarte(30, 0, null, 2700, BridesChaleur: 30) }]).Should().BeEmpty("trop court");
        var fraiche = chaude with { Fin = T.AddHours(1), MecanismeCarte = new MecanismeCarte(600, 0, null, 2700) };
        CarteChaudeEnPartie.Juger([chaude, fraiche]).Should().BeEmpty("la dernière partie fait foi");
    }

    // Une carte AMD ou Intel Arc a sa température relevée mais aucune raison de freinage (NVML est
    // NVIDIA) : le constat dit qu'il ne voit pas, au lieu de se taire comme si tout allait bien.
    [Fact]
    public void Sans_raisons_du_pilote_le_constat_dit_qu_il_ne_voit_pas()
    {
        var amd = new CarteChaudeEnPartie.PartieLue("cod.exe", T, null, 88);
        var c = CarteChaudeEnPartie.Juger([amd]).Single();
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("NVIDIA").And.Contain("88 °C");
        CarteChaudeEnPartie.Juger([]).Should().BeEmpty("aucune partie : rien à dire");
        var nvidiaCourte = new CarteChaudeEnPartie.PartieLue("cod.exe", T, new MecanismeCarte(20, 0, null, 2700), 70);
        CarteChaudeEnPartie.Juger([nvidiaCourte]).Should().BeEmpty("une NVIDIA lue trop peu de temps n'est pas « non lue »");
    }
}
