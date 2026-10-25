using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Scoring;

// -----------------------------------------------------------------------
// Le test d'invariance : qu'est-ce qui change et ne doit rien changer ?
//
// Brancher le classifieur sur le catalogue de leviers introduit un risque precis et facile a ne
// pas voir : que le /100 se mette a dependre de ce qui a ete mesure. Le score d'une machine
// changerait alors selon qu'un jeu tournait ou non au moment du scan, et deux scans de la meme
// machine mal reglee ne donneraient pas le meme chiffre.
//
// Le contrat, ecrit dans ScanScorer depuis l'origine : le /100 est un score de CONFIGURATION, le
// bloc mesure en est tenu a l'ecart. La mesure change ce qu'on RACONTE, jamais ce qu'on compte.
//
// Ces tests font varier la mesure du tout au tout — GPU-bound, CPU-bound, pression memoire,
// capee, pas de mesure du tout — et exigent que le score, les sous-scores, le grand livre et la
// couverture ne bougent pas d'un point.
// -----------------------------------------------------------------------
public sealed class MeasurementDoesNotMoveTheScoreTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, IsX3D: true, CcdCount: 2),
        new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "551.86", TriState.Off) },
        new[] { new MemoryModule("PN", "G.Skill", 4000, 3800) },
        new MotherboardInfo("MSI", "PRO B760-P"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    // Une machine DELIBEREMENT mal reglee : XMP off, plan Balanced, coeurs parques, ReBAR off.
    // Un score de 100 ne prouverait rien ici — il faut des penalites a faire bouger.
    private static SettingsState BadlyTuned() => new(
        XmpExpo: TriState.Off,
        ReBar: TriState.Off,
        PowerPlanGuid: Balanced,
        Hags: TriState.Off,
        GameMode: TriState.On,
        ParkedCoreIndexes: new[] { 8, 9, 10 },
        Vrr: TriState.Unknown);

    private static IReadOnlyList<double> Frames(double meanMs, int n = 600)
    {
        var ft = new List<double>(n);
        for (var i = 0; i < n; i++) ft.Add(meanMs);
        return ft;
    }

    private static BenchmarkResult Measured(
        double? gpuBusyMs, double? gpuWaitRatio = 0.03, double? commitPressure = 0.40) =>
        new(AvgFps: 142.9, P1LowFps: 120, P01LowFps: 90,
            FrameTimesMs: Frames(7.0),
            EstimatedPcLatencyMs: 3.0, MarginOfError: 1.0, Runs: 5,
            MeanGpuBusyMs: gpuBusyMs,
            GpuWaitRatio: gpuWaitRatio,
            PresentStallMsP95: 0.2,
            RenderPresentLatencyMsP95: 3.0,
            AnimationErrorMsP95: 0.5,
            CommitPressure: commitPressure);

    /// <summary>Les mesures les plus differentes qu'on puisse presenter au meme scan.</summary>
    public static TheoryData<string, BenchmarkResult?> WildlyDifferentMeasurements() => new()
    {
        { "aucune mesure", null },
        { "GPU-bound", Measured(7.0 * 0.96) },
        { "CPU-bound franc", Measured(7.0 * 0.45, gpuWaitRatio: 0.40) },
        { "pression memoire", Measured(7.0 * 0.96, commitPressure: 0.95) },
        { "GPU busy non mesure", Measured(null) },
    };

    private static ScanResult Score(BenchmarkResult? baseline) =>
        new ScanScorer().Score(Hw(), BadlyTuned(), baseline).Value!;

    [Theory]
    [MemberData(nameof(WildlyDifferentMeasurements))]
    public void The_ledger_is_the_same_whatever_was_measured(string label, BenchmarkResult? baseline)
    {
        var reference = Score(null).Ledger!.Select(e => (e.LeverId, e.Penalty)).ToList();

        Score(baseline).Ledger!.Select(e => (e.LeverId, e.Penalty)).Should().Equal(reference, label);
    }


    [Theory]
    [MemberData(nameof(WildlyDifferentMeasurements))]
    public void The_ledger_deducts_exactly_the_same_points_whatever_was_measured(
        string label, BenchmarkResult? baseline)
    {
        var reference = Score(null).Ledger!
            .Select(e => (e.LeverId, e.Penalty, e.State)).OrderBy(x => x.LeverId).ToList();
        var actual = Score(baseline).Ledger!
            .Select(e => (e.LeverId, e.Penalty, e.State)).OrderBy(x => x.LeverId).ToList();

        actual.Should().Equal(reference, label);
    }

    [Theory]
    [MemberData(nameof(WildlyDifferentMeasurements))]
    public void Coverage_is_the_same_whatever_was_measured(string label, BenchmarkResult? baseline)
    {
        // La couverture dit combien de leviers ont pu etre LUS. Une mesure de jeu n'y change rien,
        // et si elle y changeait quelque chose, l'honnetete du scan serait fonction du hasard.
        Score(baseline).Coverage.Should().Be(Score(null).Coverage, label);
    }

    [Fact]
    public void And_yet_the_verdict_itself_does_change_otherwise_this_test_proves_nothing()
    {
        // Le controle POSITIF. Sans lui, ces trois theories passeraient aussi bien si le
        // classifieur n'etait branche nulle part -- elles prouveraient l'invariance d'un cablage
        // absent, ce qui est exactement le genre de test vert qui ne garantit rien.
        Score(null).Bottleneck!.Value.State.Should().Be(BottleneckState.UnknownPartial);
        Score(Measured(7.0 * 0.96)).Bottleneck!.Value.State.Should().Be(BottleneckState.GpuBound);
        Score(Measured(7.0 * 0.45, gpuWaitRatio: 0.40)).Bottleneck!.Value.State
            .Should().Be(BottleneckState.CpuBoundStrong);
        Score(Measured(7.0 * 0.96, commitPressure: 0.95)).Bottleneck!.Value.State
            .Should().Be(BottleneckState.RamCommitPressure);
    }
}
