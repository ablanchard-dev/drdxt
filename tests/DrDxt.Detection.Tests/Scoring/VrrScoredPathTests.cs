using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>
/// Two gaps in an otherwise well-covered lever (19 tests already set <c>vrrCapable</c>).
///
/// 1. Two guarantees were already pinned separately: <c>ScoringRulesTests</c> asserts the
///    lever DECLARES a gain of 5, and <c>ScanScorerTests</c> asserts the total score gets
///    LOWER. Neither links them — a lever could declare 5 and subtract 3, and both would
///    still pass. This pins the link: the points actually removed from the /100 equal the
///    magnitude the scoring model publishes.
///
/// 2. The combination "VRR unreadable ON a confirmed-capable display" was never exercised:
///    existing cases pair an unreadable VRR state with an unconfirmed display. Knowing the
///    panel is capable must not turn "I cannot read it" into a deficit.
///
/// Context worth keeping: <c>SettingsState.VrrCapableDisplay</c> was written NOWHERE in src until
/// 10/09/2026 (L8 suite) — it is now read from the NVIDIA driver (NvAPI_Disp_GetVRRInfo) on the
/// game display, and stays Unknown when the driver cannot be read.
/// </summary>
public class VrrScoredPathTests
{
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "24.10.1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"), new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState State(TriState vrr, TriState vrrCapable) =>
        new(TriState.On, TriState.On, HighPerf, TriState.On, TriState.On, Array.Empty<int>(), vrr, vrrCapable);

    private static ScanResult Score(TriState vrr, TriState vrrCapable) =>
        new ScanScorer().Score(Hw(), State(vrr, vrrCapable), null).Value!;

    [Fact]
    public void Vrr_off_costs_exactly_the_five_points_the_lever_declares()
    {
        var withVrr = Score(TriState.On, TriState.On);
        var without = Score(TriState.Off, TriState.On);

        (withVrr.Score - without.Score).Should().Be(5,
            "the lever declares magnitude 5; asserting only 'lower' would pass at any magnitude");
    }

    [Fact]
    public void An_unreadable_vrr_state_never_deducts_even_on_a_capable_display()
    {
        var unknown = Score(TriState.Unknown, TriState.On);
        var optimal = Score(TriState.On, TriState.On);

        unknown.Score.Should().Be(optimal.Score,
            "knowing the panel is capable must not turn 'I cannot read VRR' into a deficit");
    }
}
