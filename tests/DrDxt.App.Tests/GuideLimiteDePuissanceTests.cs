using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Measurement.Cpu;
using FluentAssertions;

namespace DrDxt.App.Tests;

// En partie, le jeu peut être limité par le processeur, bridé par sa limite de puissance soutenue. La page
// Processeur dit en clair, hors de « Pourquoi ? », ce qui bride, où est le réglage, le risque, et comment
// revenir ; DrDXT ne décide rien.
public sealed class GuideLimiteDePuissanceTests
{
    private static readonly CpuBaseline Mesure = new("m", DateTimeOffset.UtcNow, 8, 1807.7, 1265.4, 2.4,
        PuissanceSoutenueW: 63.4, PuissanceEcartPercent: 3);

    private static CpuDeficitSoutenu Deficit(bool limite) =>
        new(30.0, 2.4, true, "x", limite, Mesure);

    private static readonly IReadOnlyList<BiosSettingReading> Bios =
        [new BiosSettingReading("CPU Cooler Tuning", 1, "Intel Default Settings", true)];

    [Fact]
    public void Une_limite_de_puissance_mesuree_et_lue_donne_le_guide_complet()
    {
        var guide = ScanViewModel.GuideLimiteDePuissance(Deficit(limite: true), Bios,
            k => k == "Cpu.Limite.Mesure" ? k + " {0} {1}" : k + " {0}");

        guide.Should().NotBeNull();
        guide!.Should().HaveCount(4);
        guide[0].Should().Contain("63").And.Contain("30");
        guide[1].Should().Contain("Intel Default Settings");
    }

    // Ce qui est noté avec chaque mesure, pour reconnaître un réglage changé au scan suivant.
    [Fact]
    public void Le_reglage_de_puissance_lu_se_note_en_une_chaine()
    {
        ScanViewModel.ReglageDePuissanceLu(
        [
            new BiosSettingReading("CPU Cooler Tuning", 1, "Intel Default Settings", true),
            new BiosSettingReading("Long Duration Power Limit (PL1)", 0, "Auto", true),
            new BiosSettingReading("XMP", 1, "Profile 1", false),
        ]).Should().Be("Intel Default Settings, PL1 Auto");
        ScanViewModel.ReglageDePuissanceLu([new BiosSettingReading("XMP", 1, "Profile 1", false)]).Should().BeNull("témoin : rien de la puissance lu");
        ScanViewModel.ReglageDePuissanceLu(null).Should().BeNull();
    }

    // Retenu dès la première seconde, sans baisse ; le guide s'ouvre sur la fréquence, pas sur un déficit.
    [Fact]
    public void Retenu_des_le_depart_le_guide_s_ouvre_sur_la_frequence()
    {
        var reference = Mesure with { FrequenceUnCoeurMhz = 5292, FrequenceSoutenueMhz = 3698 };
        var bride = new CpuDeficitSoutenu(-10.2, 1.8, false, "x", true, reference, BrideDesLeDepart: true);

        var guide = ScanViewModel.GuideLimiteDePuissance(bride, Bios, k => k == "Cpu.Limite.DesLeDepart" ? k + " {0} {1} {2}" : k + " {0}");

        guide.Should().NotBeNull().And.HaveCount(4);
        guide![0].Should().Be("Cpu.Limite.DesLeDepart 63 3698 5292");
        ScanViewModel.GuideLimiteDePuissance(bride with { BrideDesLeDepart = false }, Bios, k => k)
            .Should().BeNull("témoin : ni baisse démontrée, ni bride dès le départ");
    }

    // La bride se mesure partout ; sur une carte dont DrDXT ne lit pas le BIOS, le guide reste,
    // avec le nom générique du réglage, au lieu de se taire.
    [Fact]
    public void Sans_BIOS_lu_la_bride_mesuree_donne_le_guide_generique()
    {
        var guide = ScanViewModel.GuideLimiteDePuissance(Deficit(limite: true), [], k => k == "Cpu.Limite.Mesure" ? k + " {0} {1}" : k);
        guide.Should().NotBeNull().And.HaveCount(4);
        guide![1].Should().Be("Cpu.Limite.ReglageInconnu");
        guide[2].Should().Be("Cpu.Limite.RisqueGenerique");
        guide[3].Should().Be("Cpu.Limite.RetourGenerique");
        ScanViewModel.GuideLimiteDePuissance(Deficit(limite: true), null, k => k).Should().NotBeNull();
    }

    [Fact]
    public void Sans_limite_de_puissance_demontree_rien_n_est_affiche()
    {
        ScanViewModel.GuideLimiteDePuissance(Deficit(limite: false), Bios, k => k).Should().BeNull("témoin : baisse sans puissance plate");
        ScanViewModel.GuideLimiteDePuissance(null, Bios, k => k).Should().BeNull("aucune mesure");
    }
}
