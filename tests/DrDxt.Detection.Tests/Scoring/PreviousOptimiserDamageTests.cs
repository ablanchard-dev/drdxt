using DrDxt.Core;
using DrDxt.Detection.Scoring;
using DrDxt.Detection.Settings;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Scoring;

// -----------------------------------------------------------------------
// Les degats deja infliges par un « optimiseur » precedent.
//
// Trois regressions, trois traitements, et la difference est le sujet de ces tests : on ne
// penalise que ce que la recherche du depot source comme un vrai risque.
//
//   pagefile off  -> penalise. Microsoft documente gels et plantages a la limite de validation.
//   MMCSS a 0     -> penalise. Source comme placebo pouvant degrader l'audio, sans gain prouve.
//   SysMain off   -> SIGNALE, jamais penalise. note memoire (note du projet, non publiee) : gain non prouve, effet negligeable
//                    sur SSD. Retirer des points la fabriquerait un deficit -- la faute exacte
//                    que l'audit adverse reproche (C2).
//
// Et aucune des trois ne promet d'images : ce sont des reparations, pas des gains.
// -----------------------------------------------------------------------
public sealed class PreviousOptimiserDamageTests
{
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "Intel", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "24.10.1", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "PRO B760-P"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    // Machine saine par ailleurs : tout ce qui bouge dans un test donne est le seul suspect.
    private static SettingsState State(
        TriState pagefile = TriState.On,
        TriState mmcss = TriState.On,
        TriState sysMain = TriState.On) => new(
        XmpExpo: TriState.On,
        ReBar: TriState.On,
        PowerPlanGuid: HighPerf,
        Hags: TriState.On,
        GameMode: TriState.On,
        ParkedCoreIndexes: Array.Empty<int>(),
        Vrr: TriState.Unknown,
        PagefileConfigured: pagefile,
        DisplayRefresh: new RefreshHeadroom(TriState.Off, 200, 200),
        SysMain: sysMain,
        MmcssIntact: mmcss);

    private static ScanResult Score(SettingsState state) =>
        new ScanScorer().Score(Hw(), state, null).Value!;

    [Fact]
    public void A_healthy_machine_still_scores_100()
    {
        // Le controle de base : ces trois leviers ne doivent rien retirer a une machine intacte.
        Score(State()).Ledger!.Sum(e => e.Penalty).Should().Be(0);
    }

    // ====================== penalise ======================

    [Fact]
    public void A_disabled_pagefile_costs_points_and_lands_in_stability_not_performance()
    {
        var r = Score(State(pagefile: TriState.Off));

        r.Ledger!.Sum(e => e.Penalty).Should().BeGreaterThan(0);
        r.Findings.Should().Contain(f => f.TitleKey == "Finding.Memory.PagefileOff");
        // Desactiver le pagefile ne coute pas des images, ca coute des plantages. Le ranger en
        // Performance le vendrait comme un gain de FPS.
        r.Ledger!.Where(e => e.Group == "Stability").Sum(e => e.Penalty).Should().BeGreaterThan(0);
        r.Ledger!.Where(e => e.Group == "Performance").Sum(e => e.Penalty).Should().Be(0);
    }

    [Fact]
    public void A_zeroed_multimedia_scheduler_costs_points()
    {
        var r = Score(State(mmcss: TriState.Off));

        r.Ledger!.Sum(e => e.Penalty).Should().BeGreaterThan(0);
        r.Findings.Should().Contain(f => f.TitleKey == "Finding.Latency.MmcssBroken");
        r.Ledger!.Where(e => e.Group == "Latency").Sum(e => e.Penalty).Should().BeGreaterThan(0);
    }

    // ====================== signale, jamais penalise ======================

    [Fact]
    public void A_disabled_SysMain_is_reported_but_costs_NOTHING()
    {
        // Le test qui garde l'honnetete de ce lot. SysMain desactive est le geste « optimiseur »
        // le plus courant, donc la tentation de le compter comme un defaut est forte. La
        // recherche dit non : gain non prouve, effet negligeable sur SSD.
        var r = Score(State(sysMain: TriState.Off));

        r.Ledger!.Sum(e => e.Penalty).Should().Be(0, "on ne fabrique pas un deficit sur un reglage a l'impact nul");
        r.Findings.Should().Contain(f => f.TitleKey == "Finding.Memory.SysMainOff",
            "mais l'utilisateur apprend que sa machine a ete « optimisee » par quelqu'un d'autre");
        r.Ledger!.Should().NotContain(e => e.LeverId == "sysmain",
            "un levier non note n'a rien a faire dans le grand livre des points retires");
    }

    [Fact]
    public void SysMain_does_not_weigh_on_the_honesty_coverage_either()
    {
        // Hors du score et hors du denominateur : un levier qu'on ne note pas ne doit pas faire
        // croire que le scan a moins bien couvert la machine.
        Score(State(sysMain: TriState.Off)).Coverage
            .Should().Be(Score(State(sysMain: TriState.On)).Coverage);
    }

    // ====================== ne jamais conclure sur une lecture ratee ======================

    [Fact]
    public void An_unread_pagefile_or_scheduler_deducts_nothing_but_shows_in_coverage()
    {
        var r = Score(State(pagefile: TriState.Unknown, mmcss: TriState.Unknown));

        r.Ledger!.Sum(e => e.Penalty).Should().Be(0);
        r.Coverage!.UnknownLevers.Should().BeGreaterThanOrEqualTo(2,
            "« pas lu » doit se voir, sinon un scan aveugle passe pour un scan propre");
    }

    // ====================== les inferenceurs, la ou le piege est ======================

    [Fact]
    public void A_service_start_type_of_four_means_someone_disabled_it()
    {
        WindowsDefaultsInferencer.InferSysMain(4).Should().Be(TriState.Off);
        WindowsDefaultsInferencer.InferSysMain(2).Should().Be(TriState.On);
        WindowsDefaultsInferencer.InferSysMain(3).Should().Be(TriState.On);
    }

    [Fact]
    public void An_absent_service_value_is_unknown_not_disabled()
    {
        WindowsDefaultsInferencer.InferSysMain(null).Should().Be(TriState.Unknown);
        WindowsDefaultsInferencer.InferSysMain("4").Should().Be(TriState.Unknown,
            "une valeur d'un autre type n'est pas une valeur qu'on sait lire");
    }

    [Fact]
    public void An_ABSENT_scheduler_value_is_the_windows_default_and_therefore_intact()
    {
        // Le piege de ce lot. Windows n'ecrit pas toujours SystemResponsiveness et applique son
        // defaut. Traiter l'absence en Unknown ferait ressortir « non lu » sur la quasi-totalite
        // des machines saines -- une couverture effondree pour rien.
        WindowsDefaultsInferencer.InferMmcss(null, keyReadable: true).Should().Be(TriState.On);
    }

    [Fact]
    public void An_UNREADABLE_scheduler_key_is_unknown_and_not_the_default()
    {
        // L'autre moitie du meme piege : sans le booleen d'ouverture de cle, « la valeur n'y est
        // pas » et « je n'ai rien pu lire » se confondent, et l'un des deux est un mensonge.
        WindowsDefaultsInferencer.InferMmcss(null, keyReadable: false).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Only_100_disables_the_scheduler_and_values_below_10_are_clamped_to_20()
    {
        // learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service : « Values below 10 and
        // above 100 are clamped to 20. A value of 100 disables MMCSS. » Même règle que le docteur (MmcssReader).
        WindowsDefaultsInferencer.InferMmcss(100, keyReadable: true).Should().Be(TriState.Off);
        WindowsDefaultsInferencer.InferMmcss(0, keyReadable: true).Should().Be(TriState.On, "0 est ramené à 20 : sans effet");
        WindowsDefaultsInferencer.InferMmcss(20, keyReadable: true).Should().Be(TriState.On);
    }
}
