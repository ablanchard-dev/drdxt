using DrDxt.Core;
using DrDxt.Detection.Gpu.Intel;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// -----------------------------------------------------------------------
// NS-5 première tranche. Le test qui compte ici est le piège de nommage : Intel appelle
// « Arc » à la fois ses cartes discrètes (A770) et des iGPU soudés au CPU (« Arc Graphics »,
// « Arc 140V »). Classer un iGPU comme une carte discrète ferait proposer de l'overclocker.
// -----------------------------------------------------------------------
public class IntelGpuAdvisorTests
{
    private static GpuInfo Gpu(string model, string vendor, long vram, TriState rebar = TriState.Unknown) =>
        new(model, vendor, vram, "31.0.101.5522", rebar);

    private static GpuInfo ArcA770(TriState rebar = TriState.Unknown) =>
        Gpu("Intel(R) Arc(TM) A770 Graphics", "Intel Corporation", 16L << 30, rebar);

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel(R) Arc(TM) A380 Graphics")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics")]
    public void Recognises_a_discrete_arc_card(string model) =>
        IntelGpuAdvisor.Classify(Gpu(model, "Intel Corporation", 8L << 30))
            .Should().Be(IntelGpuClass.DiscreteArc);

    [Theory]
    [InlineData("Intel(R) Arc(TM) Graphics", 0L)]        // Meteor Lake iGPU
    [InlineData("Intel(R) Arc(TM) 140V GPU", 0L)]        // Lunar Lake iGPU — pas de lettre de série
    [InlineData("Intel(R) UHD Graphics 770", 0L)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 0L)]
    public void Does_not_mistake_an_integrated_gpu_for_a_discrete_arc(string model, long vram) =>
        IntelGpuAdvisor.Classify(Gpu(model, "Intel Corporation", vram))
            .Should().Be(IntelGpuClass.Integrated);

    [Fact]
    public void An_arc_branded_igpu_that_reports_dedicated_memory_is_still_integrated()
    {
        // Le test qui fait mordre le motif de nommage. Sans lui, le garde-fou VRAM suffirait à
        // faire passer les cas ci-dessus et un motif relâché (chercher juste « Arc ») passerait
        // les tests sans qu'on le voie — un test qui nomme une garantie sans la prouver.
        // Ici l'iGPU rapporte 2 Gio de mémoire réservée par le BIOS : seul le motif décide.
        IntelGpuAdvisor.Classify(Gpu("Intel(R) Arc(TM) Graphics", "Intel Corporation", 2L << 30))
            .Should().Be(IntelGpuClass.Integrated);
        IntelGpuAdvisor.Classify(Gpu("Intel(R) Arc(TM) 140V GPU", "Intel Corporation", 2L << 30))
            .Should().Be(IntelGpuClass.Integrated);
    }

    [Fact]
    public void An_arc_name_without_dedicated_vram_is_still_integrated()
    {
        // Deuxième garde-fou : même si le nom porte une lettre+3 chiffres, sans VRAM dédiée
        // ce n'est pas une carte. On ne propose pas de tuning sur la foi d'une chaîne.
        IntelGpuAdvisor.Classify(Gpu("Intel(R) Arc(TM) A370M Graphics", "Intel Corporation", 0))
            .Should().Be(IntelGpuClass.Integrated);
    }

    [Fact]
    public void Nvidia_and_amd_are_not_intel()
    {
        IntelGpuAdvisor.Classify(Gpu("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30))
            .Should().Be(IntelGpuClass.NotIntel);
        IntelGpuAdvisor.Classify(Gpu("AMD Radeon RX 7800 XT", "AMD", 16L << 30))
            .Should().Be(IntelGpuClass.NotIntel);
    }

    [Fact]
    public void Says_out_loud_that_there_is_no_intel_tuning_path()
    {
        // §22.3 : le refus de tuning doit être DIT. Aujourd'hui il est silencieux —
        // GpuGating.IsTunableDiscreteGpu n'autorise que NVIDIA/AMD et la carte disparaît.
        var advice = IntelGpuAdvisor.Advise(new[] { ArcA770(TriState.On) });

        advice.Select(a => a.Id).Should().Contain("gpu.intel.no_tuning_path");
    }

    [Fact]
    public void Surfaces_rebar_when_it_is_off_because_intel_calls_it_required()
    {
        var advice = IntelGpuAdvisor.Advise(new[] { ArcA770(TriState.Off) });

        advice.Should().Contain(a => a.Id == "gpu.intel.rebar_required"
                                  && a.MessageKey == "Finding.Gpu.IntelRebarOff");
    }

    [Fact]
    public void Surfaces_rebar_when_it_is_UNKNOWN_with_a_distinct_message()
    {
        // Sur Intel la lecture ReBAR n'est pas câblée. « Je ne sais pas » sur un levier qui
        // vaut deux chiffres de pourcentage se dit — et ne se déguise pas en « c'est bon ».
        var advice = IntelGpuAdvisor.Advise(new[] { ArcA770(TriState.Unknown) });

        advice.Should().Contain(a => a.Id == "gpu.intel.rebar_required"
                                  && a.MessageKey == "Finding.Gpu.IntelRebarUnknown");
    }

    [Fact]
    public void Stays_quiet_about_rebar_when_it_is_already_on()
    {
        IntelGpuAdvisor.Advise(new[] { ArcA770(TriState.On) })
            .Select(a => a.Id).Should().NotContain("gpu.intel.rebar_required");
    }

    [Fact]
    public void Always_flags_the_driver_because_it_is_the_number_one_arc_lever()
    {
        // Notre détecteur de fraîcheur ne peut pas juger un pilote Arc (plancher trop
        // grossier pour sa cadence). Le silence ne doit pas se lire « ton pilote va bien ».
        IntelGpuAdvisor.Advise(new[] { ArcA770(TriState.On) })
            .Select(a => a.Id).Should().Contain("gpu.intel.driver_is_the_lever");
    }

    [Fact]
    public void Says_nothing_about_integrated_gpus_or_other_vendors()
    {
        IntelGpuAdvisor.Advise(new[]
        {
            Gpu("Intel(R) UHD Graphics 770", "Intel Corporation", 0),
            Gpu("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30),
        }).Should().BeEmpty();
    }

    [Fact]
    public void Every_piece_of_advice_is_guidance_only_and_cites_intel()
    {
        var advice = IntelGpuAdvisor.Advise(new[] { ArcA770(TriState.Off) });

        advice.Should().HaveCount(3);
        advice.Should().OnlyContain(a => a.IsGuidanceOnly);
        advice.Should().OnlyContain(a => a.SourceUrl.Contains("intel.com")
                                      || a.SourceUrl.Contains("intel.github.io"));
        advice.Should().OnlyContain(a => a.Gpu.Contains("A770"));
    }
}

// -----------------------------------------------------------------------
// Le câblage : sans lui l'advisor serait du code mort et le produit continuerait de se taire.
// -----------------------------------------------------------------------
public class IntelFindingsInScanTests
{
    private static HardwareInventory Machine(params GpuInfo[] gpus) => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        gpus,
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    [Fact]
    public void An_arc_machine_gets_its_intel_findings_in_the_scan()
    {
        var hw = Machine(new GpuInfo("Intel(R) Arc(TM) A770 Graphics", "Intel Corporation", 16L << 30, "31.0.101.5522", TriState.Off));

        var findings = ScoringRules.Evaluate(hw, State);

        findings.Should().Contain(f => f.TitleKey == "Finding.Gpu.IntelNoTuningPath");
        findings.Should().Contain(f => f.TitleKey == "Finding.Gpu.IntelRebarOff" && f.Severity == Severity.Minor);
        findings.Should().Contain(f => f.TitleKey == "Finding.Gpu.IntelDriverIsTheLever");
    }

    [Fact]
    public void Intel_findings_never_invent_a_gain_number_and_are_never_paid()
    {
        // Intel (intel.com) donne « variable selon le titre » et « potentiellement deux chiffres » :
        // ni l'un ni l'autre n'est promettable sur cette machine ⇒ null, jamais un chiffre.
        var hw = Machine(new GpuInfo("Intel(R) Arc(TM) A770 Graphics", "Intel Corporation", 16L << 30, "31.0.101.5522", TriState.Off));

        var intel = ScoringRules.Evaluate(hw, State).Where(f => f.TitleKey.StartsWith("Finding.Gpu.Intel")).ToList();

        intel.Should().HaveCount(3);
        intel.Should().OnlyContain(f => f.EstimatedGainPct == null);
    }

    [Fact]
    public void An_nvidia_machine_gets_no_intel_findings()
    {
        var hw = Machine(new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "32.0.16.1088", TriState.On));

        ScoringRules.Evaluate(hw, State).Should().NotContain(f => f.TitleKey.StartsWith("Finding.Gpu.Intel"));
    }
}
