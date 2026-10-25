using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// Fail-closed materiel, et la matrice avant le paiement.
//
// Le probleme est commercial autant que technique : vendre du tuning GPU a quelqu'un dont la
// carte est en lecture seule, c'est un remboursement immediat et un avis negatif. Le produit
// savait deja refuser au moment d'APPLIQUER (sonde d'ecriture-identite) -- mais apres l'achat,
// et derriere une elevation.
//
// Le defaut trouve en ecrivant ce lot : `SupportsVoltageUndervolt` rendait `true` pour tout GPU
// non-NVIDIA, au motif que le verrou Blackwell ne concerne pas AMD. C'etait vrai et insuffisant.
// Ca laissait passer RDNA3 -- la seule architecture AMD qui garde le voltage manuel, et la seule
// que personne n'a jamais testee en ecriture. Un test encodait meme ce comportement.
// -----------------------------------------------------------------------
public sealed class GpuTuningCoverageTests
{
    private static GpuInfo Gpu(string model, string vendor = "AMD", long vram = 16L << 30) =>
        new(model, vendor, vram, "24.10.1", TriState.On);

    private static HardwareInventory Hw(ChassisType chassis = ChassisType.Desktop, params GpuInfo[] gpus) =>
        new(new CpuInfo("CPU", "Intel", 8, 16, false, 1),
            gpus.Length > 0 ? gpus : new[] { Gpu("AMD Radeon RX 9070 XT") },
            new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
            new MotherboardInfo("MSI", "B760"),
            new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
            chassis);

    // ====================== le if que le fail-closed demande ======================

    [Fact]
    public void RDNA3_never_gets_manual_voltage_because_nobody_has_ever_write_tested_it()
    {
        // Le test du lot. Ecrire un voltage sur du materiel jamais valide est exactement ce que
        // « fail-closed materiel » interdit -- et c'est ce que faisait le code.
        GpuArchitectureGate.SupportsVoltageUndervolt(Gpu("AMD Radeon RX 7900 XTX"))
            .Should().BeFalse();
    }

    [Fact]
    public void RDNA4_does_get_it_because_it_was_validated_live()
    {
        // Valide EN VIE sur une RX 9070 XT : ecriture, revert, empreinte propre. Refuser ici
        // serait aussi faux qu'accepter sur RDNA3 -- le fail-closed n'est pas un refus general.
        GpuArchitectureGate.SupportsVoltageUndervolt(Gpu("AMD Radeon RX 9070 XT"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", GpuArchitecture.Rdna3)]
    [InlineData("AMD Radeon RX 7600", GpuArchitecture.Rdna3)]
    [InlineData("AMD Radeon RX 9070 XT", GpuArchitecture.Rdna4)]
    [InlineData("NVIDIA GeForce RTX 4060", GpuArchitecture.AdaOrOlder)]
    [InlineData("NVIDIA GeForce RTX 5080", GpuArchitecture.Blackwell)]
    // Les RX 6000 (RDNA2) passaient « inconnues » ; elles sont reconnues.
    [InlineData("AMD Radeon RX 6800 XT", GpuArchitecture.Rdna2)]
    [InlineData("AMD Radeon RX 6600", GpuArchitecture.Rdna2)]
    [InlineData("AMD Radeon RX 6700 XT", GpuArchitecture.Rdna2)]
    // RX 5000 (RDNA1), RX 400/500 et Vega (GCN) : reconnues par le nom, comme les autres.
    [InlineData("AMD Radeon RX 5700 XT", GpuArchitecture.Rdna1)]
    [InlineData("AMD Radeon RX 5500 XT", GpuArchitecture.Rdna1)]
    [InlineData("Radeon RX 580 Series", GpuArchitecture.Gcn)]
    [InlineData("AMD Radeon RX 480", GpuArchitecture.Gcn)]
    [InlineData("AMD Radeon RX 590 GME", GpuArchitecture.Gcn)]
    [InlineData("AMD Radeon RX 550X", GpuArchitecture.Gcn)]
    [InlineData("Radeon RX Vega 64", GpuArchitecture.Gcn)]
    [InlineData("AMD Radeon VII", GpuArchitecture.Gcn)]
    [InlineData("Une carte que personne ne connait", GpuArchitecture.Unknown)]
    public void The_classifier_reads_the_marketing_name_and_says_Unknown_when_it_cannot(
        string model, GpuArchitecture expected)
    {
        // Le nom commercial est le seul signal lisible sans elevation et sans driver, donc le
        // seul utilisable avant l'achat. Il ne prouve rien sur le pilote -- d'ou la sonde.
        GpuArchitectureGate.ClassifyByName(model).Should().Be(expected);
    }

    [Fact]
    public void The_architecture_regex_actually_matches_a_real_card_name()
    {
        // Ecrit apres avoir livre une regex ou le `\b` etait devenu un vrai caractere backspace :
        // elle compilait, ne matchait jamais, et la garde RDNA3 ne se declenchait donc jamais.
        // Une garde qui ne se declenche pas est indiscernable d'une garde absente.
        GpuArchitectureGate.ClassifyByName("AMD Radeon RX 7900 XTX").Should().Be(GpuArchitecture.Rdna3);
        GpuArchitectureGate.ClassifyByName("TRX 7900").Should().Be(GpuArchitecture.Unknown,
            "la frontiere de mot doit tenir : « TRX » n'est pas « RX »");
    }

    // ====================== la matrice ======================

    // Aucune architecture n'a encore son rapport archivé ; sans lui, lecture seule.
    // La RTX 4060 (Ada) a son rapport ; RDNA4 et Blackwell n'en ont pas encore. Autocontrôle : la
    // classe AdaOrOlder couvre aussi les RTX 20/30 et GTX 10/16, que ce rapport ne prouve pas.
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER")]
    [InlineData("AMD Radeon RX 9070 XT")]
    [InlineData("NVIDIA GeForce RTX 5080")]
    public void Sans_rapport_archive_une_carte_reste_en_lecture_seule(string modele)
    {
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu(modele, modele.StartsWith("NVIDIA") ? "NVIDIA" : "AMD", 16L << 30)))
            .Should().ContainSingle().Subject;

        row.Support.Should().Be(GpuTuningSupport.ReadOnly);
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonNeverWriteTested);
    }

    [Fact]
    public void A_validated_card_is_sold_as_tunable()
    {
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30)),
                new HashSet<GpuArchitecture> { GpuArchitecture.AdaOrOlder })
            .Should().ContainSingle().Subject;

        row.Support.Should().Be(GpuTuningSupport.Validated);
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonValidated);
        row.VoltageTuning.Should().BeTrue();
    }

    // AMD-TOUT : une RX 6000 est reconnue, lue, jamais validée en écriture : lecture seule avec la bonne raison, voltage fermé.
    [Theory]
    [InlineData("AMD Radeon RX 6600")]
    [InlineData("AMD Radeon RX 6700 XT")]
    public void Une_carte_RDNA2_est_reconnue_en_lecture_seule_jamais_ecrite(string modele)
    {
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu(modele))).Should().ContainSingle().Subject;
        row.Architecture.Should().Be(GpuArchitecture.Rdna2);
        row.Support.Should().Be(GpuTuningSupport.ReadOnly);
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonNeverWriteTested);
        row.VoltageTuning.Should().BeFalse();
    }

    [Fact]
    public void An_RDNA3_card_is_sold_as_READ_ONLY_and_says_why()
    {
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu("AMD Radeon RX 7900 XTX")))
            .Should().ContainSingle().Subject;

        row.Support.Should().Be(GpuTuningSupport.ReadOnly);
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonNeverWriteTested);
        row.VoltageTuning.Should().BeFalse();
    }

    [Fact]
    public void A_card_nobody_recognises_is_READ_ONLY_not_optimistically_supported()
    {
        // Le fail-closed dans sa forme la plus litterale : ne pas savoir n'autorise rien.
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu("AMD Radeon Carte Inconnue")))
            .Should().ContainSingle().Subject;

        row.Support.Should().Be(GpuTuningSupport.ReadOnly);
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonUnknownArchitecture);
    }

    // La même règle pour toutes les architectures Radeon : sans rapport, lecture seule et voltage fermé ; avec un
    // rapport validé, la carte s'ouvre. Le voltage reste fermé là où la garde le ferme : un rapport ouvre les leviers
    // qu'il prouve, pas la tension.
    [Theory]
    [InlineData("AMD Radeon RX 5700 XT", GpuArchitecture.Rdna1, false)]
    [InlineData("Radeon RX 580 Series", GpuArchitecture.Gcn, false)]
    [InlineData("AMD Radeon RX 6700 XT", GpuArchitecture.Rdna2, false)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuArchitecture.Rdna3, false)]
    [InlineData("AMD Radeon RX 9070 XT", GpuArchitecture.Rdna4, true)]
    public void Une_Radeon_suit_la_meme_regle_rapport_valide_ouvre_sinon_lecture_seule(
        string modele, GpuArchitecture architecture, bool voltage)
    {
        var sans = GpuTuningCoverage.For(Hw(gpus: Gpu(modele)), new HashSet<GpuArchitecture>()).Single();
        sans.Architecture.Should().Be(architecture);
        sans.Support.Should().Be(GpuTuningSupport.ReadOnly);
        sans.ReasonKey.Should().Be(GpuTuningCoverage.ReasonNeverWriteTested);
        sans.VoltageTuning.Should().Be(voltage);

        var avec = GpuTuningCoverage.For(Hw(gpus: Gpu(modele)), new HashSet<GpuArchitecture> { architecture }).Single();
        avec.Support.Should().Be(GpuTuningSupport.Validated);
        avec.ReasonKey.Should().Be(GpuTuningCoverage.ReasonValidated);
        avec.VoltageTuning.Should().Be(voltage);
    }

    [Fact]
    public void Un_rapport_RDNA2_n_ouvre_pas_une_RDNA3()
    {
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu("AMD Radeon RX 7900 XTX")),
            new HashSet<GpuArchitecture> { GpuArchitecture.Rdna2 }).Single();
        row.Support.Should().Be(GpuTuningSupport.ReadOnly);
    }

    [Fact]
    public void Blackwell_keeps_its_tuning_and_loses_ONLY_the_voltage()
    {
        // Classer toute la carte en lecture seule perdrait des leviers reels (limite de
        // puissance, frequences) sur une generation par ailleurs reglable.
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu("NVIDIA GeForce RTX 5080", "NVIDIA", 16L << 30)),
                new HashSet<GpuArchitecture> { GpuArchitecture.Blackwell })
            .Should().ContainSingle().Subject;

        row.Support.Should().Be(GpuTuningSupport.Validated);
        row.VoltageTuning.Should().BeFalse();
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonVoltageLockedByVendor);
    }

    [Fact]
    public void A_laptop_is_told_its_firmware_locks_the_power_profile()
    {
        // Ce n'est pas un manque du produit, c'est une propriete de la machine — et le dire
        // evite un achat decu.
        var row = GpuTuningCoverage
            .For(Hw(ChassisType.Laptop, Gpu("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30)))
            .Should().ContainSingle().Subject;

        row.Support.Should().Be(GpuTuningSupport.NotApplicable);
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonLaptopOrHandheld);
        row.VoltageTuning.Should().BeFalse();
    }

    [Fact]
    public void An_integrated_GPU_is_not_sold_anything()
    {
        var row = GpuTuningCoverage.For(Hw(gpus: Gpu("Intel UHD Graphics 770", "Intel", 0)))
            .Should().ContainSingle().Subject;

        row.Support.Should().Be(GpuTuningSupport.NotApplicable);
        row.ReasonKey.Should().Be(GpuTuningCoverage.ReasonNotTunableHardware);
    }

    [Fact]
    public void A_machine_with_two_cards_gets_one_honest_line_each()
    {
        var rows = GpuTuningCoverage.For(Hw(gpus: new[]
        {
            Gpu("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30),
            Gpu("AMD Radeon RX 7900 XTX"),
        }), new HashSet<GpuArchitecture> { GpuArchitecture.AdaOrOlder });

        rows.Should().HaveCount(2);
        rows[0].Support.Should().Be(GpuTuningSupport.Validated);
        rows[1].Support.Should().Be(GpuTuningSupport.ReadOnly);
    }

    [Fact]
    public void Every_row_carries_a_reason_so_a_refusal_is_never_unexplained()
    {
        var rows = GpuTuningCoverage.For(Hw(gpus: new[]
        {
            Gpu("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30),
            Gpu("AMD Radeon RX 7900 XTX"),
            Gpu("Intel UHD Graphics 770", "Intel", 0),
        }));

        rows.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.ReasonKey));
    }

    [Fact]
    public void Une_RTX_4060_avec_son_rapport_archive_est_validee()
        => GpuTuningCoverage.For(Hw(gpus: Gpu("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30))).Single().Support
            .Should().Be(GpuTuningSupport.Validated);
}
