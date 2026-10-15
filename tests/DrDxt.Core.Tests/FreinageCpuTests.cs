using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>Les secondes du processeur, avec les registres de la machine de référence (TjMax 100 °C).</summary>
public class FreinageCpuTests
{
    private const ulong Tj100 = 100UL << 16;
    private static ulong Paquet(int ecart, params int[] bits) => ((ulong)ecart << 16) | bits.Aggregate(0UL, (a, b) => a | (1UL << b));
    private static ulong Coeur(int ecart, params int[] bits) => Paquet(ecart, bits) | (1UL << 31);

    [Fact]
    public void Le_paquet_donne_le_coeur_le_plus_chaud_et_le_coeur_0_le_sien()
    {
        // Relevé du 12/10 00:14 : paquet à 98 °C, cœur 0 à 77 °C, bit 12 (limite de courant) levé.
        var e = EtatCpu.Decoder(Tj100, Paquet(2), Coeur(23, 12))!;

        e.ChaudC.Should().Be(98);
        e.Coeur0C.Should().Be(77);
        e.LimiteCourant.Should().BeTrue();
        e.Chaleur.Should().BeFalse();
        e.LimiteW.Should().BeFalse();
    }

    [Fact]
    public void Chaleur_et_prochot_viennent_du_paquet_ou_du_coeur_la_limite_de_puissance_du_paquet_seul()
    {
        EtatCpu.Decoder(Tj100, Paquet(0, 0, 2, 10), null)!.Should().Be(new EtatCpu(100, null, true, true, true, false));
        // Le bit 10 du cœur dit « sous la fréquence demandée, toute raison », pas « limite de puissance ».
        EtatCpu.Decoder(Tj100, Paquet(5), Coeur(10, 0, 10))!.Should().Be(new EtatCpu(95, 90, true, false, false, false));
    }

    [Fact]
    public void Un_coeur_sans_lecture_valide_ou_des_registres_absurdes_ne_disent_rien()
    {
        EtatCpu.Decoder(Tj100, Paquet(10), Paquet(20, 12))!.Coeur0C.Should().BeNull("bit 31 absent : lecture non valide");
        EtatCpu.Decoder(Tj100, Paquet(10), Paquet(20, 12))!.LimiteCourant.Should().BeFalse();
        EtatCpu.Decoder(0, Paquet(10), null).Should().BeNull();
        EtatCpu.Decoder(Tj100, Paquet(120), null).Should().BeNull();
    }

    [Fact]
    public void Les_secondes_se_comptent_et_les_captures_se_cumulent_au_poids_des_secondes()
    {
        var a = FreinageCpu.Depuis([new EtatCpu(100, 80, true, false, false, true), new EtatCpu(90, 70, false, false, true, true)])!;
        a.Should().Be(new FreinageCpu(2, 1, 0, 1, 2, 95, 100, 75));

        var b = FreinageCpu.Depuis([new EtatCpu(60, null, false, false, false, false)])!;
        var tout = FreinageCpu.Cumuler([a, null, b])!;

        tout.Secondes.Should().Be(3);
        tout.LimiteCourant.Should().Be(2);
        tout.ChaudMoyenC.Should().BeApproximately(250.0 / 3, 1e-9);
        tout.ChaudMaxC.Should().Be(100);
        tout.Coeur0MoyenC.Should().Be(75, "seules les captures qui ont lu le cœur comptent pour lui");
        tout.Part(tout.LimiteCourant).Should().BeApproximately(2.0 / 3, 1e-9);

        FreinageCpu.Depuis([]).Should().BeNull();
        FreinageCpu.Cumuler([null]).Should().BeNull();
    }

    // Le résumé garde le
    // ventilateur CPU PENDANT les secondes freinées par la chaleur, ce que le docteur juge (« il ne poussait pas »).
    [Fact]
    public void Le_ventilateur_CPU_est_garde_pendant_les_secondes_freinees_par_la_chaleur()
    {
        EtatCpu Seconde(bool chaud, int pwm, int trMin) => new(chaud ? 100 : 80, 70, chaud, false, false, false, new VentilateurLu(0, trMin, pwm));
        var f = FreinageCpu.Depuis([Seconde(true, 50, 1200), Seconde(true, 60, 1300), Seconde(false, 100, 2000), new EtatCpu(100, 70, true, false, false, false)])!;

        f.SecondesVentiloChaud.Should().Be(2, "deux secondes chaudes portent le ventilateur, la troisième ne l'a pas lu");
        f.VentiloPwmChaud.Should().Be(55);
        f.VentiloTrMinChaud.Should().Be(1250);

        var cumul = FreinageCpu.Cumuler([f, FreinageCpu.Depuis([Seconde(true, 85, 1800), Seconde(true, 85, 1800)])])!;
        cumul.SecondesVentiloChaud.Should().Be(4);
        cumul.VentiloPwmChaud.Should().Be(70, "pondéré par les secondes : (55×2 + 85×2) / 4");
        FreinageCpu.Depuis([Seconde(false, 40, 900)])!.VentiloPwmChaud.Should().BeNull("aucune seconde chaude : rien à juger");
    }
}
