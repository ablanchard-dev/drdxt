using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>
/// Deux trous dans un levier par ailleurs bien couvert (19 tests posent déjà <c>vrrCapable</c>).
///
/// 1. Deux garanties étaient figées séparément : <c>ScoringRulesTests</c> vérifie que le levier
///    déclare un gain de 5, et <c>ScanScorerTests</c> que le score total baisse. Aucune ne les
///    relie : un levier pourrait déclarer 5 et retirer 3, et les deux passeraient encore. Ceci
///    fige le lien : les points vraiment retirés du /100 égalent l'ampleur que le modèle de
///    notation publie.
///
/// 2. La combinaison « VRR illisible sur un écran confirmé capable » n'était jamais exercée :
///    les cas existants associent un état VRR illisible à un écran non confirmé. Savoir l'écran
///    capable ne doit pas transformer « je ne peux pas le lire » en déficit.
///
/// Contexte utile : <c>SettingsState.VrrCapableDisplay</c> n'était écrit nulle part dans src ;
/// il est lu depuis le pilote NVIDIA (NvAPI_Disp_GetVRRInfo) sur l'écran du jeu,
/// et reste Unknown quand le pilote ne peut pas être lu.
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

        (without.Ledger!.Sum(e => e.Penalty) - withVrr.Ledger!.Sum(e => e.Penalty)).Should().Be(5,
            "the lever declares magnitude 5; asserting only 'lower' would pass at any magnitude");
    }

    [Fact]
    public void An_unreadable_vrr_state_never_deducts_even_on_a_capable_display()
    {
        var unknown = Score(TriState.Unknown, TriState.On);
        var optimal = Score(TriState.On, TriState.On);

        unknown.Ledger!.Sum(e => e.Penalty).Should().Be(optimal.Ledger!.Sum(e => e.Penalty),
            "knowing the panel is capable must not turn 'I cannot read VRR' into a deficit");
    }
}
