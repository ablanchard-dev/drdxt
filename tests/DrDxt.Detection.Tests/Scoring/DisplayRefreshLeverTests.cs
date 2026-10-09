using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Scoring;

// -----------------------------------------------------------------------
// L1.4 — le levier le plus gratuit, enfin cable.
//
// Le plan du levier (non publie) portait « heuristique ecrite et prouvee, LEVIER NON CABLE »
// depuis le 15/08/2026. Sur la machine de reference, un panneau 200 Hz tournait a 143 Hz pendant
// trois jours pendant qu'un scan la declarait saine. L'heuristique avait ses 9 tests ; le produit,
// lui, n'en disait pas un mot.
//
// Ces tests ne re-testent pas l'heuristique : ils testent que le SCAN la prononce, et surtout
// qu'un ecran illisible ne devient jamais un ecran sain.
// -----------------------------------------------------------------------
public sealed class DisplayRefreshLeverTests
{
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "Intel", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "24.10.1", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "PRO B760-P"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState State(RefreshHeadroom? refresh) => new(
        XmpExpo: TriState.On,
        ReBar: TriState.On,
        PowerPlanGuid: HighPerf,
        Hags: TriState.On,
        GameMode: TriState.On,
        ParkedCoreIndexes: Array.Empty<int>(),
        Vrr: TriState.Unknown,
        DisplayRefresh: refresh);

    private static ScanResult Score(RefreshHeadroom? refresh) =>
        new ScanScorer().Score(Hw(), State(refresh), null).Value!;

    [Fact]
    public void A_screen_running_below_its_maximum_is_finally_said_out_loud()
    {
        // Le cas de la machine de reference : 200 Hz de panneau, 143 Hz a l'ecran. 28 % des
        // images que le GPU rend deja sont jetees, et aucun autre levier ne compense ca.
        var r = Score(new RefreshHeadroom(TriState.On, 143, 200));

        r.Findings.Should().Contain(f => f.TitleKey == "Finding.Display.RefreshBelowMax");
        r.Score.Should().BeLessThan(100, "un ecran sous-exploite est un vrai deficit, pas un detail");
    }

    [Fact]
    public void The_deficit_is_attributable_to_a_named_lever_in_the_ledger()
    {
        var entry = Score(new RefreshHeadroom(TriState.On, 143, 200)).Ledger!
            .Should().ContainSingle(e => e.LeverId == "display-refresh").Subject;

        entry.Penalty.Should().BeGreaterThan(0);
        entry.State.Should().Be(LeverState.Suboptimal);
    }

    [Fact]
    public void A_screen_already_at_its_maximum_costs_nothing()
    {
        var r = Score(new RefreshHeadroom(TriState.Off, 200, 200));

        r.Score.Should().Be(100);
        r.Findings.Should().NotContain(f => f.TitleKey == "Finding.Display.RefreshBelowMax");
    }

    [Fact]
    public void A_one_hertz_gap_is_not_a_deficit()
    {
        // Windows rapporte un mode 144 Hz comme 143 (le timing reel est ~143,98 et l'API tronque).
        // Sans cette tolerance, le produit harcelerait un ecran deja au maximum. Le scan doit
        // heriter de cette regle, pas seulement l'heuristique isolee.
        var r = Score(DisplayRefreshHeuristicHeadroom(currentHz: 143, bestHz: 144));

        r.Score.Should().Be(100);
    }

    [Fact]
    public void An_unreadable_display_is_NEVER_reported_as_a_healthy_one()
    {
        // La ligne a ne pas franchir. « Je n'ai pas pu enumerer les modes » n'est pas « l'ecran
        // est deja au maximum ». Les confondre transformerait ce levier en satisfecit automatique
        // sur toute machine dont l'affichage ne se lit pas — l'inverse exact de sa raison d'etre.
        var r = Score(new RefreshHeadroom(TriState.Unknown, 143, 0));

        r.Score.Should().Be(100, "un inconnu ne deduit jamais de points");
        r.Ledger!.Should().ContainSingle(e => e.LeverId == "display-refresh")
            .Which.State.Should().Be(LeverState.Unknown);
        r.Coverage!.UnknownLevers.Should().BeGreaterThan(0, "et il doit se VOIR dans la couverture");
    }

    [Fact]
    public void No_display_probe_at_all_leaves_the_lever_uncovered_not_optimal()
    {
        // Une machine sans sonde d'affichage n'a pas un bon ecran : elle a un ecran non lu.
        var r = Score(null);

        r.Ledger!.Should().ContainSingle(e => e.LeverId == "display-refresh")
            .Which.State.Should().Be(LeverState.Unknown);
        r.Coverage!.KnownLevers.Should().BeLessThan(r.Coverage.ApplicableLevers);
    }

    // Passe par l'heuristique reelle plutot que de fabriquer le verdict a la main : c'est elle
    // qui porte la tolerance de 1 Hz, et la recopier ici la ferait diverger en silence.
    private static RefreshHeadroom DisplayRefreshHeuristicHeadroom(int currentHz, int bestHz) =>
        DrDxt.Detection.Display.DisplayRefreshHeuristic.Evaluate(currentHz, new[] { bestHz });
}
