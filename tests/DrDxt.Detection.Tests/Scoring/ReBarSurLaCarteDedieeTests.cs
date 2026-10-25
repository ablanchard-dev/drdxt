using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>
/// Le Resizable BAR est un réglage de la carte dédiée : une puce intégrée n'a pas de BAR à agrandir. Un bureau à puce
/// intégrée seule n'a donc pas ce levier, et quand la puce intégrée est listée devant une vraie carte, c'est la carte
/// qui se lit.
/// </summary>
public class ReBarSurLaCarteDedieeTests
{
    private static readonly GpuInfo Uhd = new("Intel(R) UHD Graphics 630", "Intel", 128L << 20, "31.0", TriState.Unknown);
    private static readonly GpuInfo Rtx = new("RTX 4060", "NVIDIA", 8L << 30, "616.56", TriState.Off);

    private static HardwareInventory Hw(params GpuInfo[] cartes) => new(
        new CpuInfo("Intel Core i5-9500", "Intel", 6, 6, false, 1),
        cartes,
        new[] { new MemoryModule("M378A1K43CB2-CTD", "Samsung", 2666, 2666) },
        new MotherboardInfo("Dell Inc.", "0XXXXX"),
        new BiosInfo("1.0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState State() => new(TriState.Unknown, TriState.Unknown, "381b4222-f694-41f0-9685-ff5bb260df2e", TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: null, Vrr: TriState.Unknown);

    [Fact]
    public void Puce_integree_seule_le_levier_rebar_est_sans_objet()
    {
        var rebar = LeverCatalog.Evaluate(Hw(Uhd), State()).Single(l => l.LeverId == "rebar");

        rebar.State.Should().Be(LeverState.NotApplicable);
        rebar.Finding.Should().BeNull();
    }

    [Fact]
    public void Puce_integree_listee_devant_la_carte_le_rebar_se_lit_sur_la_carte()
    {
        var rebar = LeverCatalog.Evaluate(Hw(Uhd, Rtx), State()).Single(l => l.LeverId == "rebar");

        rebar.State.Should().Be(LeverState.Suboptimal);
    }
}
