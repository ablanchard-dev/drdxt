using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// AMD-TOUT (09/10/2026) : toute la gamme AMD, du bas au haut, reçoit les constats et les leviers de son matériel, pas
/// seulement la grosse carte. Quatre machines synthétiques dans tests/Shared/machines (RX 6600 + Ryzen 5 5600 en DDR4,
/// RX 6700 XT + Ryzen 5 7600, RX 7800 XT + 7800X3D, RX 7900 XTX + 7950X3D), rejouées ici sur le vrai catalogue.
/// Les vraies Radeon le prouveront ensuite (rapports rangés par tools/ranger_rapport.py).
/// </summary>
public sealed class GammeAmdTests
{
    public static TheoryData<string, GpuArchitecture, bool> Gamme() => new()
    {
        { "amd-rx6600-ryzen5-5600", GpuArchitecture.Rdna2, false },
        { "amd-rx6700xt-ryzen5-7600", GpuArchitecture.Rdna2, false },
        { "amd-radeon-synthetique", GpuArchitecture.Rdna3, false },
        { "amd-rx7900xtx-ryzen9-7950x3d", GpuArchitecture.Rdna3, true },
    };

    private static IReadOnlyList<ITweak> VraisLeviers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDiagnosticsLog>());
        services.AddSingleton(Substitute.For<IBenchmarkRunner>());
        services.AddDrDxtApplication();
        return services.BuildServiceProvider().GetRequiredService<ITweakCatalog>().All().ToList();
    }

    [Theory]
    [MemberData(nameof(Gamme))]
    public void La_carte_est_reconnue_et_dite_en_lecture_seule_sans_tension(string nom, GpuArchitecture architecture, bool _)
    {
        var hw = MachinesEnregistreesTests.Charger(nom).Inventaire;
        var ligne = GpuTuningCoverage.For(hw).Single();
        ligne.Architecture.Should().Be(architecture);
        // Aucune Radeon n'a encore de rapport de validation archivé : lecture seule, et la tension reste fermée.
        ligne.Support.Should().Be(GpuTuningSupport.ReadOnly);
        ligne.VoltageTuning.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Gamme))]
    public void Les_leviers_de_son_materiel_s_ouvrent_et_aucun_levier_NVIDIA(string nom, GpuArchitecture _, bool deuxCcdX3d)
    {
        var m = MachinesEnregistreesTests.Charger(nom);
        var ouverts = VraisLeviers().Where(t => t.AppliesTo(m.Inventaire, m.Reglages)).Select(t => t.Id).ToList();

        ouverts.Should().Contain(["gpu.power_limit", "latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn"]);
        // Lus seulement par le pilote NVIDIA : jamais proposés sur une Radeon.
        ouverts.Should().NotContain(["latency.prefer_max_perf", "gpu.repos_global_normal", "latency.reflex"]);
        // Le routage X3D ne concerne que les X3D à deux CCD (7950X3D) ; un 7800X3D n'a qu'un CCD.
        (ouverts.Contains("fixit.x3d_ccd")).Should().Be(deuxCcdX3d);
        // X3D-PLAN (09/10/2026) : même avec le processeur mesuré en goulot, un X3D à deux blocs garde Équilibré (le pilote AMD
        // y range le jeu sur le bloc à cache) ; les autres machines reçoivent le plan.
        var cpuLimite = m.Reglages with { GoulotMesure = DrDxt.Core.BottleneckState.CpuBoundStrong, PowerPlanGuid = KnownPowerPlans.Balanced.ToString() };
        VraisLeviers().Single(t => t.Id == "fixit.power_plan").AppliesTo(m.Inventaire, cpuLimite).Should().Be(!deuxCcdX3d);
    }

    [Theory]
    [MemberData(nameof(Gamme))]
    public void Le_docteur_dit_la_memoire_le_X3D_et_ce_qu_il_ne_lit_pas_sur_AMD(string nom, GpuArchitecture _, bool deuxCcdX3d)
    {
        var m = MachinesEnregistreesTests.Charger(nom);
        var scan = new ScanScorer().Score(m.Inventaire, m.Reglages, null).Value!.Findings;
        var bios = new DrDxt.Bios.BiosAdvisor().Advise(m.Inventaire, m.Reglages).Value!;

        // Chaque machine de la gamme tourne sous la fréquence de son kit (XMP/EXPO coupé) : le docteur le dit.
        scan.Should().Contain(f => f.TitleKey == "Finding.Memory.XmpOff");
        // Au repos rien n'est parqué : le docteur dit le routage X3D « non confirmé », le levier le propose sous garde.
        (scan.Any(f => f.TitleKey == "Finding.Scheduling.X3dUnknown")).Should().Be(deuxCcdX3d);
        (bios.Any(b => b.Kind == FirmwareSettingKind.X3dCppc)).Should().Be(deuxCcdX3d);

        var constats = scan.Select(Constat.Depuis).Concat(m.Sante.Select(Constat.Depuis)).ToList();
        constats.Should().Contain(c => c.Id == "sante/THERMIQUE/temperature du GPU" && c.Etat == EtatConstat.NonLu);
    }
}

/// <summary>
/// AMD-TOUT (3, 09/10/2026) : le mode Expert de la carte reste ouvert sur une Radeon (le joueur décide, le bail et le test
/// de stabilité rendent la carte), mais l'accord dit que l'écriture n'a jamais été essayée sur cette architecture.
/// </summary>
public sealed class ExpertSurCarteNonValideeTests
{
    [Theory]
    [InlineData("amd-rx6600-ryzen5-5600", true)]
    [InlineData("amd-rx7900xtx-ryzen9-7950x3d", true)]
    [InlineData("reference-i7-14700f-rtx4060-2026-10-08", false)]
    public async Task L_accord_dit_quand_la_carte_n_a_jamais_ete_essayee_en_ecriture(string nom, bool nonValidee)
    {
        var vm = MachinesEnregistreesTests.Vm(MachinesEnregistreesTests.Charger(nom));
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LabelExpertAccord.Contains("Carte.Expert.NonValideIci").Should().Be(nonValidee);
        vm.LabelExpertAccord.Should().StartWith("Carte.Expert.Accord");
    }
}

/// <summary>
/// Consoles portables (demande relevée par l'audit du 09/10/2026) : une machine synthétique ROG Ally (Ryzen Z1 Extreme,
/// Radeon 780M intégrée, LPDDR5 soudée). DrDXT n'y règle ni la carte (intégrée, châssis verrouillé : dit avec son motif),
/// ni la mémoire, ni le BIOS ; seuls les réglages de Windows sans risque s'ouvrent.
/// </summary>
public sealed class ConsolePortableTests
{
    [Fact]
    public void Une_console_portable_ne_recoit_que_les_reglages_de_Windows_et_le_dit()
    {
        var m = MachinesEnregistreesTests.Charger("console-portable-rog-ally");
        m.Inventaire.Chassis.Should().Be(ChassisType.Handheld);

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDiagnosticsLog>());
        services.AddSingleton(Substitute.For<IBenchmarkRunner>());
        services.AddDrDxtApplication();
        var ouverts = services.BuildServiceProvider().GetRequiredService<ITweakCatalog>().All()
            .Where(t => t.AppliesTo(m.Inventaire, m.Reglages)).Select(t => t.Id).ToList();
        ouverts.Should().BeEquivalentTo(["latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn"]);

        GpuTuningCoverage.For(m.Inventaire).Should().ContainSingle()
            .Which.ReasonKey.Should().Be(GpuTuningCoverage.ReasonLaptopOrHandheld);
        new DrDxt.Bios.BiosAdvisor().Advise(m.Inventaire, m.Reglages).Value!
            .Should().OnlyContain(b => b.TitleKey == "Bios.NotApplicable.Title");
        new ScanScorer().Score(m.Inventaire, m.Reglages, null).Value!.Findings
            .Should().NotContain(f => f.TitleKey.StartsWith("Finding.Memory"), "mémoire soudée : aucun XMP à activer");
    }
}

/// <summary>
/// La gamme NVIDIA et Intel au-delà de la machine de référence (09/10/2026) : GTX 1650 + i5-10400F (Turing, DDR4),
/// RTX 3060 + i5-12400F (Ampere), RTX 5070 + i5-13400F (Blackwell). Chaque carte est reconnue ; aucune n'a de rapport
/// de validation (seule une RTX 40 en a un) ; Blackwell garde la tension fermée par NVIDIA ; la limite de puissance se
/// propose ; aucun levier AMD ni X3D ; la mémoire sous son profil est dite.
/// </summary>
public sealed class GammeNvidiaIntelTests
{
    public static TheoryData<string, GpuArchitecture, bool> Gamme() => new()
    {
        { "nvidia-gtx1650-i5-10400", GpuArchitecture.AdaOrOlder, true },
        { "nvidia-rtx3060-i5-12400f", GpuArchitecture.AdaOrOlder, true },
        { "nvidia-rtx5070-i5-13400f", GpuArchitecture.Blackwell, false },
    };

    [Theory]
    [MemberData(nameof(Gamme))]
    public void Chaque_carte_est_reconnue_et_ce_qui_s_ouvre_est_le_sien(string nom, GpuArchitecture architecture, bool tension)
    {
        var m = MachinesEnregistreesTests.Charger(nom);
        var ligne = GpuTuningCoverage.For(m.Inventaire).Single();
        ligne.Architecture.Should().Be(architecture);
        ligne.Support.Should().Be(GpuTuningSupport.ReadOnly, "aucun rapport de validation archivé hors RTX 40");
        ligne.VoltageTuning.Should().Be(tension);

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDiagnosticsLog>());
        services.AddSingleton(Substitute.For<IBenchmarkRunner>());
        services.AddDrDxtApplication();
        var ouverts = services.BuildServiceProvider().GetRequiredService<ITweakCatalog>().All()
            .Where(t => t.AppliesTo(m.Inventaire, m.Reglages)).Select(t => t.Id).ToList();
        ouverts.Should().Contain("gpu.power_limit").And.NotContain("fixit.x3d_ccd");

        new ScanScorer().Score(m.Inventaire, m.Reglages, null).Value!.Findings
            .Should().Contain(f => f.TitleKey == "Finding.Memory.XmpOff");
    }
}
