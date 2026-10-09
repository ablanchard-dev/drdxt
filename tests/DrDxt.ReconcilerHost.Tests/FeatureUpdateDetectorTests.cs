using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// T27 — FeatureUpdateDetector.WasFeatureUpdateInstalledAsync:
//   read current build (IOsBuildProbe) vs ReconcilerState.LastFeatureUpdateBuild.
//   different → Ok(true) AND persist the new LastFeatureUpdateBuild (so the trigger
//   fires once per feature update, not every tick).
//   same → Ok(false), no write.
//   probe/store Fail → Fail (no guess).
// -----------------------------------------------------------------------
public sealed class FeatureUpdateDetectorTests
{
    private static FeatureUpdateDetector Make(IOsBuildProbe probe, IReconcilerStateStore store)
        => new(probe, store);

    [Fact]
    public async Task WhenBuildChanged_ReturnsTrue_AndPersistsNewBuild()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.4000" };
        var store = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var detector = Make(probe, store);

        var result = await detector.WasFeatureUpdateInstalledAsync(default);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeTrue();
        store.Saved!.LastFeatureUpdateBuild.Should().Be("22631.4000");
        store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task WhenFirstRun_EmptyLastBuild_TreatsAsChanged_AndPersists()
    {
        // First install: LastFeatureUpdateBuild is empty. A non-empty current build is
        // "new" — record it so the NEXT tick with the same build is a no-op.
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var store = new InMemoryReconcilerStateStore();
        var detector = Make(probe, store);

        var result = await detector.WasFeatureUpdateInstalledAsync(default);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeTrue();
        store.Saved!.LastFeatureUpdateBuild.Should().Be("22631.3447");
    }

    [Fact]
    public async Task WhenBuildUnchanged_ReturnsFalse_NoWrite()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var store = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var detector = Make(probe, store);

        var result = await detector.WasFeatureUpdateInstalledAsync(default);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeFalse();
        store.SaveCount.Should().Be(0, "an unchanged build must not write");
    }

    [Fact]
    public async Task WhenProbeFails_ReturnsFail_NoWrite()
    {
        var probe = new FakeOsBuildProbe { Fail = true };
        var store = new InMemoryReconcilerStateStore();
        var detector = Make(probe, store);

        var result = await detector.WasFeatureUpdateInstalledAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.RegistryFail);
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task WhenStoreLoadFails_ReturnsFail()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.4000" };
        var store = new InMemoryReconcilerStateStore { FailLoad = true };
        var detector = Make(probe, store);

        var result = await detector.WasFeatureUpdateInstalledAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
    }

    [Fact]
    public async Task WhenStoreSaveFails_OnChange_ReturnsFail()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.4000" };
        var store = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" })
        { FailSave = true };
        var detector = Make(probe, store);

        var result = await detector.WasFeatureUpdateInstalledAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
    }
}
