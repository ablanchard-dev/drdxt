using System.Collections.Generic;
using System.Linq;
using DrDxt.App;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// AppliedTweakManifestMapper — turns the KEPT outcomes of a validated apply into the
// reconciler's AppliedTweak desired-state entries.
//
// Two impedance mismatches it bridges (both real, both reviewed as an explicit table):
//   * id NAMESPACE: the Application catalog keys tweaks by ids like "fixit.power_plan"
//     / "latency.reflex"; the reconciler (policy + descriptor catalog) keys by
//     "fixit.sanePowerPlan" / "latency.reflex.lowLatencyMode". The mapper translates.
//   * DESIRED value: the ApplyReport snapshot carries only the original value (the
//     inverse op); the canonical DESIRED token the reconciler re-applies is known per
//     tweak and lives in this closed table.
//
// Only KEPT (Applied && !AutoReverted) outcomes that map to a reconcilable surface are
// emitted. Auto-reverted, unmapped (e.g. GPU-tuning), and non-reconcilable tweaks are
// excluded — the manifest holds only validated, reconcilable tweaks.
// -----------------------------------------------------------------------
public sealed class AppliedTweakManifestMapperTests
{
    private static ApplyOutcome Kept(string id, string originalJson = "{}")
        => new(id, Applied: true, AutoReverted: false, new TweakSnapshot(id, originalJson), Error: null);

    private static ApplyOutcome AutoReverted(string id)
        => new(id, Applied: true, AutoReverted: true, new TweakSnapshot(id, "{}"), Error: null);

    private static ApplyReport Report(params ApplyOutcome[] outcomes)
        => new(outcomes.ToList(), Verdict: null);

    [Fact]
    public void Maps_keptPowerPlan_toReconcilerIdAndDesiredToken()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(Kept("fixit.power_plan", "\"balanced\"")));

        tweaks.Should().ContainSingle();
        var t = tweaks[0];
        t.TweakId.Should().Be("fixit.sanePowerPlan", "the reconciler keys power plan by this id");
        t.DesiredValueJson.Should().Be(AppliedTweakManifestMapper.HighPerformancePlanToken,
            "the desired token is High Performance — what the reconciler re-applies");
        t.OriginalValueJson.Should().Be("\"balanced\"", "the original is taken from the kept snapshot");
    }

    [Fact]
    public void Maps_keptReflex_toReconcilerIdAndUltraToken()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(Kept("latency.reflex")));

        tweaks.Should().ContainSingle();
        tweaks[0].TweakId.Should().Be("latency.reflex.lowLatencyMode");
        tweaks[0].DesiredValueJson.Should().Be("2", "Ultra low-latency = 2");
    }

    [Fact]
    public void Maps_keptX3d_toReconcilerServiceIdAndRunningToken()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(Kept("fixit.x3d_ccd")));

        tweaks.Should().ContainSingle();
        tweaks[0].TweakId.Should().Be("fixit.x3dVCacheService");
        tweaks[0].DesiredValueJson.Should().Be("running");
    }

    [Fact]
    public void Maps_keptHags_toReconcilerIdAndOnToken()
    {
        var tweaks = new AppliedTweakManifestMapper().ToDesiredTweaks(Report(Kept("latency.hagsOn")));

        tweaks.Should().ContainSingle();
        tweaks[0].TweakId.Should().Be("os.hags");
        tweaks[0].DesiredValueJson.Should().Be("2", "HAGS on = HwSchMode 2");
    }

    [Fact]
    public void Excludes_hkcuTweaks_from_localSystem_reconcile()
    {
        // The boot reconciler runs as LocalSystem (HKLM-only). The HKCU NS-1 tweaks — Game DVR
        // (HKCU DWORD), Game Mode (composite HKCU), VRR/flip (merged HKCU REG_SZ) — are
        // intentionally excluded: a LocalSystem HKCU write hits SYSTEM's hive. Only HAGS (HKLM)
        // is committed to the manifest. They persist natively + the App re-applies them.
        var tweaks = new AppliedTweakManifestMapper().ToDesiredTweaks(Report(
            Kept("latency.hagsOn"),
            Kept("latency.gameDvrBackgroundOff"),
            Kept("latency.gameModeOn"),
            Kept("latency.vrrFlipModelOn")));

        tweaks.Select(t => t.TweakId).Should().BeEquivalentTo(new[] { "os.hags" });
    }

    [Fact]
    public void Excludes_autoRevertedTweaks()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(
            Kept("fixit.power_plan"),
            AutoReverted("latency.reflex"))); // measured regression → reverted → not committed

        tweaks.Select(t => t.TweakId).Should().BeEquivalentTo(new[] { "fixit.sanePowerPlan" });
    }

    [Fact]
    public void Excludes_unmappedTweaks_likeGpuTuning()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(
            Kept("fixit.power_plan"),
            Kept("gpu.undervolt"))); // not a reconcilable OS-side surface → not in the table

        tweaks.Select(t => t.TweakId).Should().BeEquivalentTo(new[] { "fixit.sanePowerPlan" });
    }

    [Fact]
    public void Excludes_outcomesWithoutASnapshot()
    {
        var mapper = new AppliedTweakManifestMapper();

        var noSnapshot = new ApplyOutcome("fixit.power_plan",
            Applied: true, AutoReverted: false, Snapshot: null, Error: null);

        mapper.ToDesiredTweaks(Report(noSnapshot)).Should().BeEmpty(
            "without a snapshot we cannot record an honest original value → exclude");
    }

    // Every desired token the mapper emits must be ACCEPTED by the production
    // reconcilability policy (so a real CommitAsync never rejects what the mapper built).
    [Fact]
    public void EveryMappedId_isReconcilableUnderTheProductionPolicy()
    {
        var mapper = new AppliedTweakManifestMapper();
        DrDxt.Reconciler.IReconcilabilityPolicy policy = new DrDxt.Reconciler.DefaultReconcilabilityPolicy();

        var tweaks = mapper.ToDesiredTweaks(Report(
            Kept("fixit.power_plan"),
            Kept("latency.reflex"),
            Kept("fixit.x3d_ccd")));

        tweaks.Should().HaveCount(3);
        foreach (var t in tweaks)
            policy.IsReconcilable(t.TweakId).Should().BeTrue($"'{t.TweakId}' must be reconcilable");
    }
}
