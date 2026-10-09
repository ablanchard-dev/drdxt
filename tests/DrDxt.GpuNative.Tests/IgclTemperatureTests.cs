using System.Runtime.InteropServices;
using DrDxt.GpuNative.Igcl;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// A5 (07/10/2026) : la température Intel par IGCL. Pas de carte Intel sur ce PC (RTX 4060) : la disposition des structures
// est figée contre igcl_api.h, et le choix de la sonde se teste sans DLL. La valeur réelle attend une machine Arc.
public sealed class IgclTemperatureTests
{
    // ctl_init_args_t : 4 + 1 (+3) + 4 + 4 + 4 + 16 = 36 ; ctl_temp_properties_t : 4 + 1 (+3) + 4 (+4) + 8 = 24.
    [Fact]
    public void Les_structures_ont_la_taille_de_l_en_tete()
    {
        Marshal.SizeOf<IgclNative.CtlInitArgs>().Should().Be(36);
        Marshal.OffsetOf<IgclNative.CtlInitArgs>(nameof(IgclNative.CtlInitArgs.ApplicationUid)).Should().Be(20);
        Marshal.SizeOf<IgclNative.CtlTempProperties>().Should().Be(24);
        Marshal.OffsetOf<IgclNative.CtlTempProperties>(nameof(IgclNative.CtlTempProperties.MaxTemperature)).Should().Be(16);
        IgclNative.Version11.Should().Be(0x0001_0001u);
    }

    [Fact]
    public void La_sonde_GPU_passe_devant_la_globale_et_la_memoire_n_est_pas_la_puce()
        => IgclNative.Choisir([(IgclNative.SondeGlobale, 80), (2, 95), (IgclNative.SondeGpu, 64.5)]).Should().Be(64.5);

    [Fact]
    public void Sans_sonde_GPU_la_globale_sert()
        => IgclNative.Choisir([(IgclNative.SondeGlobale, 58), (4, 30)]).Should().Be(58);

    // Une puce intégrée et une Arc : la plus chaude des deux.
    [Fact]
    public void Deux_cartes_Intel_rendent_la_plus_chaude()
        => IgclNative.Choisir([(IgclNative.SondeGpu, 45), (IgclNative.SondeGpu, 71)]).Should().Be(71);

    // Zéro, négatif, hors d'échelle, NaN : aucune n'est une température ; une sonde mémoire seule ou un minimum non plus.
    [Fact]
    public void Une_valeur_impossible_ou_une_sonde_hors_puce_reste_non_lue()
    {
        IgclNative.Choisir([(IgclNative.SondeGpu, 0), (IgclNative.SondeGpu, -5), (IgclNative.SondeGpu, 150),
            (IgclNative.SondeGpu, double.NaN)]).Should().BeNull();
        IgclNative.Choisir([(2, 70), (3, 40), (5, 35)]).Should().BeNull();
        IgclNative.Choisir([]).Should().BeNull();
    }
}
