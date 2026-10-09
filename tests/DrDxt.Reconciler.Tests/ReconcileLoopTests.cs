using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// T13, T15-T24, T27b — ReconcileAsync, the core invariants (I1-I7 + boot-loop).
//
// Every test drives the loop with in-memory fakes; the FakeReapplyClient is the
// Only thing that "writes" (into the FakeSettingProbe), so we can assert exactly
// which items were re-applied, which were notified, which were refused, and that
// idempotent passes do nothing.
// -----------------------------------------------------------------------
public sealed class ReconcileLoopTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static AppliedTweak Tweak(string id, string desired, string original)
        => new(id, desired, original, At);

    // A harness whose state tracks the listed items as already-applied at the given
    // build, and whose build probe reports a CHANGED build (so drift to original
    // classifies WindowsReset → re-apply).
    private static ReconcilerHarness ResetScenario(params string[] ids)
    {
        var items = ids.Select(id =>
            new ReconcilerItemState(id, false, "22631.3447", "555.99", At, "OK")).ToArray();
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        foreach (var id in ids) h.Policy.Map(id, ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000"; // build CHANGED → WindowsReset on stock value
        return h;
    }

    // T13 — IDEMPOTENCE: everything already at desired ⇒ zero writes, zero Change.
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task ReconcileAsync_WhenAllInDesiredState_DoesNothing()
    {
        var h = ResetScenario("a", "b");
        h.Probe.Set("a", "1").Set("b", "2");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0"), Tweak("b", "2", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().BeEmpty("no item drifted");
        h.Diagnostics.Entries.Should().BeEmpty("no Change is logged on a pure no-op pass");
    }

    // T13 (second leg) — repair once, then a second pass does nothing.
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task ReconcileAsync_RunTwice_SecondPassIsNoOp()
    {
        var h = ResetScenario("a");
        h.Probe.Set("a", "0"); // drifted to stock
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var first = await h.Build().ReconcileAsync(desired, default);
        first.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle().Which.tweakId.Should().Be("a");

        // Second pass: the probe now reads "1" (the FakeReapplyClient wrote it).
        var second = await h.Build().ReconcileAsync(desired, default);
        second.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle("the second pass must add NO new write");
    }

    // T15 — only the drifted item is re-applied.
    [Fact]
    public async Task ReconcileAsync_OnlyReappliesDriftedItems()
    {
        var h = ResetScenario("a", "b", "c");
        h.Probe.Set("a", "1").Set("b", "0").Set("c", "3"); // only b drifted to stock
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"), Tweak("b", "2", "0"), Tweak("c", "3", "0"),
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle().Which.tweakId.Should().Be("b");
        h.Diagnostics.Entries.Where(e => e.actor == "DrDXT").Should().ContainSingle();
    }

    // T16 — each re-application is logged with correct from/to/actor.
    [Fact]
    public async Task ReconcileAsync_LogsEachReapplication()
    {
        var h = ResetScenario("a");
        h.Probe.Set("a", "0");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        await h.Build().ReconcileAsync(desired, default);

        h.Diagnostics.Entries.Should().Contain(e =>
            e.tweakId == "a" && e.from == "0" && e.to == "1" && e.actor == "DrDXT");
    }

    // T17 — UserIntentional ⇒ NOTIFY, no overwrite, still Ok.
    [Fact]
    public async Task ReconcileAsync_UserIntentionalDrift_NotifiesAndDoesNotOverwrite()
    {
        // No build change + a third value ⇒ UserIntentional.
        var items = new[] { new ReconcilerItemState("a", false, "22631.3447", "555.99", At, "OK") };
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447"; // UNCHANGED
        h.Probe.Set("a", "9");             // ≠ desired ≠ original (user choice)
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue("notifying is not a failure");
        h.Notifier.Notified.Should().ContainSingle().Which.tweakId.Should().Be("a");
        h.Reapply.Reapplies.Should().BeEmpty("a user choice is never overwritten");
    }

    // T18 — Unknown ⇒ conservative (notify, no overwrite by default).
    [Fact]
    public async Task ReconcileAsync_UnknownDrift_DoesNotOverwriteByDefault()
    {
        var items = new[] { new ReconcilerItemState("a", false, "22631.3447", "555.99", At, "OK") };
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22631.3447";  // UNCHANGED
        h.Probe.Set("a", "0");              // == original, no build change ⇒ Unknown
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().BeEmpty();
        h.Notifier.Notified.Should().ContainSingle();
    }

    // T19 — one item fails, the others still reconcile; aggregated Fail.
    [Fact]
    public async Task ReconcileAsync_OneItemFails_OthersStillReconcile_ResultIsAggregatedFail()
    {
        var h = ResetScenario("a", "b", "c");
        // The reapply client refuses "b" (helper op fails).
        var reapply = new FakeReapplyClient(h.Probe, refuse: id => id == "b");
        h.WithReapply(reapply);
        h.Probe.Set("a", "0").Set("b", "0").Set("c", "0"); // all drifted to stock
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"), Tweak("b", "2", "0"), Tweak("c", "3", "0"),
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ReconcilePartial);
        result.Error.Value.Message.Should().Contain("b");
        // a and c succeeded; b did not.
        reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "a", "c" });
    }

    // T20 — SECURITY: a tweakId not allowlisted is refused, no IPC for it.
    [Fact]
    public async Task ReconcileAsync_NeverAppliesTweakNotInVerifiedManifest()
    {
        var h = ResetScenario("a");          // only "a" mapped in the policy
        h.Probe.Set("a", "0").Set("ghost", "0");
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"),
            Tweak("ghost", "1", "0"),        // unknown / not allowlisted
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ReconcilePartial);
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().NotContain("ghost");
        // a security log line for the refusal.
        h.Diagnostics.Entries.Should().Contain(e => e.tweakId == "ghost" && e.actor.Contains("refused"));
    }

    // T21 — write reported OK but read-back still drifted ⇒ item KO.
    [Fact]
    public async Task ReconcileAsync_WriteSucceedsButRelectReadStillDrifted_MarksItemFailed()
    {
        var h = ResetScenario("a");
        var reapply = new FakeReapplyClient(h.Probe, writeWrong: id => id == "a"); // writes a WRONG value
        h.WithReapply(reapply);
        h.Probe.Set("a", "0");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ReconcilePartial);
        reapply.Reapplies.Should().ContainSingle("the write was attempted");
        // The confirming read-back did not see desired → KO, not a false success.
    }

    // T22 — a protected surface (Defender/Update/BITS) is hard-refused. This is a "do no
    // harm" invariant as load-bearing as tamper/idempotence, so it rides the same CI hard
    // gate (Category=ReconcileGate + TreatNoTestsAsError): if this test is ever deleted or
    // its [Trait] stripped, the gate step matches fewer tests and CI fails — the guarantee
    // cannot silently vanish. Added 26/08/2026 after a red-team mutation of the refusal
    // stayed green on the gate (proven by a plain test, but not gated).
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task ReconcileAsync_RejectsProtectedSurfaces()
    {
        var h = ResetScenario("a");
        h.Policy.Map("svc.windefend", ReconcileSurface.Service).Protect("svc.windefend");
        h.Probe.Set("a", "0").Set("svc.windefend", "0");
        var desired = new DesiredState(new[]
        {
            Tweak("a", "1", "0"),
            Tweak("svc.windefend", "Stopped", "Running"),
        });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse();
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().NotContain("svc.windefend");
        h.Diagnostics.Entries.Should().Contain(e =>
            e.tweakId == "svc.windefend" && e.actor.Contains("protected"));
    }

    // T23 — per-item kill-switch: compared/logged, never re-applied.
    [Fact]
    public async Task ReconcileAsync_ItemPaused_IsComparedButNotReapplied()
    {
        var items = new[]
        {
            new ReconcilerItemState("a", true, "22631.3447", "555.99", At, "OK"),  // PAUSED
            new ReconcilerItemState("b", false, "22631.3447", "555.99", At, "OK"),
        };
        var h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        h.Policy.Map("a", ReconcileSurface.Registry).Map("b", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000"; // build changed
        h.Probe.Set("a", "0").Set("b", "0"); // both drifted to stock
        var desired = new DesiredState(new[] { Tweak("a", "1", "0"), Tweak("b", "2", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "b" });
        // a was read for diagnostics (a 'paused' log line) but not re-applied.
        h.Diagnostics.Entries.Should().Contain(e => e.tweakId == "a" && e.actor.Contains("paused"));
    }

    // T24 — global kill-switch: zero writes regardless of drift, Ok + paused report.
    [Fact]
    public async Task ReconcileAsync_GlobalPaused_SkipsAllWrites()
    {
        var h = ResetScenario("a", "b");
        await h.StateStore.SaveAsync(
            (h.StateStore.Saved ?? ReconcilerState.Default) with { GlobalPaused = true }, default);
        h.Probe.Set("a", "0").Set("b", "0");
        var desired = new DesiredState(new[] { Tweak("a", "1", "0"), Tweak("b", "2", "0") });

        var reconciler = h.Build();
        var (result, report) = await reconciler.ReconcileWithReportAsync(desired, default);

        result.IsOk.Should().BeTrue();
        report.GloballyPaused.Should().BeTrue();
        h.Reapply.Reapplies.Should().BeEmpty();
    }

    // T27b — anti boot-loop: GPU-tuning item suspended at the streak threshold,
    // safe items keep reconciling.
    [Fact]
    public async Task ReconcileAsync_BootLoopStreakReached_SuspendsGpuReapply_ButKeepsSafeItems()
    {
        var h = ResetScenario("reg.safe", "gpu.offset");
        h.Policy.Vendor("gpu.offset", "NVIDIA"); // GPU-tuning class
        h.BootLoopThreshold = 3;
        h.BootStore.Streak = 3;                  // at threshold → suspend GPU re-apply
        h.Probe.Set("reg.safe", "0").Set("gpu.offset", "0"); // both drifted to stock
        var desired = new DesiredState(new[]
        {
            Tweak("reg.safe", "1", "0"),
            Tweak("gpu.offset", "1", "0"),
        });

        var (result, report) = await h.Build().ReconcileWithReportAsync(desired, default);

        // The safe registry item is re-applied; the GPU item is SUSPENDED, not failed.
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "reg.safe" });
        report.Suspended.Select(i => i.TweakId).Should().BeEquivalentTo(new[] { "gpu.offset" });
        result.IsOk.Should().BeTrue("a suspended GPU item is a safe state, not a failure");
    }

    // T27b (below threshold) — GPU item IS re-applied when boots are healthy.
    [Fact]
    public async Task ReconcileAsync_BootLoopBelowThreshold_ReappliesGpuItem()
    {
        var h = ResetScenario("gpu.offset");
        h.Policy.Vendor("gpu.offset", "NVIDIA");
        h.BootLoopThreshold = 3;
        h.BootStore.Streak = 1; // healthy enough
        h.Probe.Set("gpu.offset", "0");
        var desired = new DesiredState(new[] { Tweak("gpu.offset", "1", "0") });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "gpu.offset" });
    }
}
