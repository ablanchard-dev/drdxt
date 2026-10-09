using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

public sealed class MemoryStabilityAdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 5, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DateTimeOffset> DaysAgo(params int[] days)
        => days.Select(d => Now.AddDays(-d)).ToList();

    [Fact]
    public void AnActiveProfileAndARecentUncleanRestartAreWorthSaying()
    {
        var advice = MemoryStabilityAdvisor.Advise(
            TriState.On, DaysAgo(2), ChassisType.Desktop, Now);

        advice.Should().ContainSingle().Which.MessageKey
            .Should().Be("Finding.Memory.XmpOnWithUncleanShutdowns");
    }

    [Fact]
    public void NeverReadIsSilence()
    {
        // null = jamais lu. Le silence est la seule reponse honnete.
        MemoryStabilityAdvisor.Advise(TriState.On, null, ChassisType.Desktop, Now)
            .Should().BeEmpty();
    }

    [Fact]
    public void ReadAndCleanIsAlsoSilence()
    {
        // Liste vide = lu, aucun redemarrage sale. « XMP est un overclock » serait vrai et
        // inutile : un avertissement permanent s'apprend a etre ignore.
        MemoryStabilityAdvisor.Advise(TriState.On, Array.Empty<DateTimeOffset>(), ChassisType.Desktop, Now)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(91)]
    [InlineData(365)]
    public void AnIncidentOlderThanTheWindowNoLongerDescribesThisMachine(int days)
    {
        MemoryStabilityAdvisor.Advise(TriState.On, DaysAgo(days), ChassisType.Desktop, Now)
            .Should().BeEmpty();
    }

    [Fact]
    public void TheWindowBoundaryIsIncluded()
    {
        MemoryStabilityAdvisor.Advise(TriState.On, DaysAgo(MemoryStabilityAdvisor.WindowDays),
            ChassisType.Desktop, Now).Should().ContainSingle();
    }

    [Theory]
    [InlineData(TriState.Off)]
    [InlineData(TriState.Unknown)]
    public void WithoutAProvenActiveProfileThereIsNothingToSuspect(TriState xmp)
    {
        // On n'accuse pas un overclock dont on ignore l'existence. XMP off avec des redemarrages
        // sales est un autre probleme, et ce n'est pas celui-ci.
        MemoryStabilityAdvisor.Advise(xmp, DaysAgo(1), ChassisType.Desktop, Now)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(ChassisType.Laptop)]
    [InlineData(ChassisType.Handheld)]
    [InlineData(ChassisType.Unknown)]
    public void OutsideADesktopTheProfileIsFirmwareLockedSoTheAdviceIsInapplicable(ChassisType chassis)
    {
        MemoryStabilityAdvisor.Advise(TriState.On, DaysAgo(1), chassis, Now).Should().BeEmpty();
    }
}

/// <summary>Meme lecon qu'en it. 3 : un conseiller que personne n'appelle n'existe pas.</summary>
public sealed class MemoryStabilityReachesTheScanTests
{
    private static HardwareInventory Desktop() => new(
        new CpuInfo("Intel Core i7-14700F", "GenuineIntel", 20, 28, false, 1),
        Array.Empty<GpuInfo>(),
        new[]
        {
            new MemoryModule("F4-4000C18-32GTES", "G.Skill", 3800, 4000,
                SmbiosMemoryType: 26, Ranks: 2, ConfiguredVoltageMv: 1350,
                DeviceLocator: "Controller0-DIMMA2"),
            new MemoryModule("F4-4000C18-32GTES", "G.Skill", 3800, 4000,
                SmbiosMemoryType: 26, Ranks: 2, ConfiguredVoltageMv: 1350,
                DeviceLocator: "Controller1-DIMMB2"),
        },
        new MotherboardInfo("MSI", "PRO B760-P WIFI DDR4"),
        new BiosInfo("1.A0", DateTimeOffset.UnixEpoch, true),
        ChassisType.Desktop);

    private static SettingsState State(IReadOnlyList<DateTimeOffset>? shutdowns) => new(
        TriState.On, TriState.Unknown, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown)
        { UncleanShutdowns = shutdowns };

    [Fact]
    public void TheStabilityAdviceReachesTheFindings()
    {
        var findings = ScoringRules.Evaluate(Desktop(), State(new[] { DateTimeOffset.UtcNow.AddDays(-3) }));

        findings.Should().Contain(f => f.TitleKey == "Finding.Memory.XmpOnSousLeKitAvecArrets");
    }

    [Fact]
    public void ItCarriesNoNumberAndCostsNoPoints()
    {
        var f = ScoringRules.Evaluate(Desktop(), State(new[] { DateTimeOffset.UtcNow.AddDays(-3) }))
            .Single(x => x.TitleKey == "Finding.Memory.XmpOnSousLeKitAvecArrets");

        f.EstimatedGainPct.Should().BeNull("rien n'est chiffre : la classe est NON CHIFFRE");
        f.Severity.Should().Be(Severity.Info, "on ne penalise pas une machine pour un soupcon");
    }

    [Fact]
    public void NotReadMeansTheScanSaysNothing()
    {
        ScoringRules.Evaluate(Desktop(), State(null))
            .Should().NotContain(f => f.TitleKey.StartsWith("Finding.Memory.XmpOn", StringComparison.Ordinal));
    }
}
