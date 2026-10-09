using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// T28 — the composed feature-update TRIGGER.
//
// The build detector (T27) is the PRIMARY, reliable signal. The IUpdateSignal event
// read (Windows Update / Setup completion in the event log) is a CORROBORATING
// signal that can fire EARLIER than the build flips (e.g. setup finished, build will
// flip on next boot). The trigger fires when EITHER says "update": primary OR
// corroborating. A primary FAIL is not masked by the corroborating signal — an
// unreadable build is surfaced as Fail (the host must not silently treat unknown as
// "updated"); the corroborating read is best-effort and never turns a primary Fail
// into Ok.
// -----------------------------------------------------------------------
public sealed class FeatureUpdateTriggerTests
{
    private static readonly DesiredState Empty = new(Array.Empty<AppliedTweak>());

    private static FeatureUpdateTrigger Make(
        IOsBuildProbe probe, IReconcilerStateStore store, IUpdateSignal signal)
        => new(new FeatureUpdateDetector(probe, store), signal);

    [Fact]
    public async Task WhenBuildChanged_FiresEvenIfEventSignalSaysNo()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.4000" };
        var store = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var trigger = Make(probe, store, signal);

        var result = await trigger.ShouldReconcileAsync(default);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task WhenBuildUnchanged_ButEventCorroborates_Fires()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var store = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = true };
        var trigger = Make(probe, store, signal);

        var result = await trigger.ShouldReconcileAsync(default);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeTrue("the corroborating event signal fires even before the build flips");
    }

    [Fact]
    public async Task WhenNeitherSignals_DoesNotFire()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var store = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var trigger = Make(probe, store, signal);

        var result = await trigger.ShouldReconcileAsync(default);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task WhenPrimaryBuildProbeFails_ReturnsFail_NotMaskedByCorroboratingSignal()
    {
        var probe = new FakeOsBuildProbe { Fail = true };
        var store = new InMemoryReconcilerStateStore();
        var signal = new FakeUpdateSignal { Recent = true };
        var trigger = Make(probe, store, signal);

        var result = await trigger.ShouldReconcileAsync(default);

        result.IsOk.Should().BeFalse("an unreadable primary build is a real failure, not 'no update'");
        result.Error!.Value.Code.Should().Be(ErrorCodes.RegistryFail);
    }

    [Fact]
    public async Task WhenCorroboratingSignalFails_ButPrimarySaysChanged_StillFires()
    {
        // Primary already proves an update; the corroborating read being unavailable
        // must not demote a proven update. (Corroborating is additive, best-effort.)
        var probe = new FakeOsBuildProbe { Build = "22631.4000" };
        var store = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Fail = true };
        var trigger = Make(probe, store, signal);

        var result = await trigger.ShouldReconcileAsync(default);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeTrue();
    }
}

/// <summary>Fake corroborating update signal. <see cref="Recent"/> is what it reports;
/// <see cref="Fail"/> makes the read fail (best-effort path).</summary>
public sealed class FakeUpdateSignal : IUpdateSignal
{
    public bool Recent { get; set; }
    public bool Fail { get; set; }

    public Task<Result<bool>> RecentSetupCompletionAsync(CancellationToken ct)
        => Task.FromResult(Fail
            ? Result<bool>.Fail(ErrorCodes.IoFailure, "event log read failed (test)")
            : Result<bool>.Ok(Recent));
}
