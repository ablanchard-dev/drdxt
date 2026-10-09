using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// T26 — ReconcileTickService.RunTickAsync:
//   * FINDING 1 — entitlement gate first: an absent/expired entitlement → do not
//     load/reconcile, log a SECURITY diagnostic, return the Fail (fail-closed moat).
//   * load via IDesiredStateStore.LoadAsync()
//   * load FAILS (tamper/schema/IO) → do not reconcile, log a SECURITY diagnostic,
//     return the Fail (fail-clean, I4 — never enter a write loop on a bad manifest)
//   * load Ok → call IReconciler.ReconcileAsync(state, ct) and return its result.
// -----------------------------------------------------------------------
public sealed class ReconcileTickServiceTests
{
    private static readonly DesiredState SampleState =
        new(new[] { new AppliedTweak("latency.reflex.lowLatencyMode", "2", "0", DateTimeOffset.UnixEpoch) });

    [Fact]
    public async Task RunTick_WhenEntitledAndLoadOk_ReconcilesThatState_AndReturnsItsResult()
    {
        var store = Substitute.For<IDesiredStateStore>();
        store.LoadAsync().Returns(Result<DesiredState>.Ok(SampleState));

        var reconciler = Substitute.For<IReconciler>();
        reconciler.ReconcileAsync(SampleState, Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        var diagnostics = new RecordingDiagnostics();
        var service = new ReconcileTickService(store, reconciler, diagnostics, new FakeEntitlementGate());

        var result = await service.RunTickAsync(CancellationToken.None);

        result.IsOk.Should().BeTrue();
        await reconciler.Received(1).ReconcileAsync(SampleState, Arg.Any<CancellationToken>());
        diagnostics.Entries.Should().BeEmpty("a clean, entitled reconcile raises no security log");
    }

    [Fact]
    public async Task RunTick_WhenLoadOk_PropagatesReconcileFailure()
    {
        var store = Substitute.For<IDesiredStateStore>();
        store.LoadAsync().Returns(Result<DesiredState>.Ok(SampleState));

        var reconciler = Substitute.For<IReconciler>();
        reconciler.ReconcileAsync(Arg.Any<DesiredState>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(ReconcilerErrorCodes.ReconcilePartial, "one item failed"));

        var service = new ReconcileTickService(store, reconciler, new RecordingDiagnostics(), new FakeEntitlementGate());

        var result = await service.RunTickAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ReconcilePartial);
    }

    [Fact]
    public async Task RunTick_WhenLoadFails_DoesNotReconcile_LogsSecurity_ReturnsTheFail()
    {
        var store = Substitute.For<IDesiredStateStore>();
        store.LoadAsync().Returns(
            Result<DesiredState>.Fail(ErrorCodes.ManifestTampered, "Manifest signature did not verify."));

        var reconciler = Substitute.For<IReconciler>();
        var diagnostics = new RecordingDiagnostics();
        var service = new ReconcileTickService(store, reconciler, diagnostics, new FakeEntitlementGate());

        var result = await service.RunTickAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);

        // I4: a bad manifest never reaches the reconciler (no write loop).
        await reconciler.DidNotReceive()
            .ReconcileAsync(Arg.Any<DesiredState>(), Arg.Any<CancellationToken>());

        // A SECURITY diagnostic was journalled (the actor marks it as a security event).
        diagnostics.Entries.Should().ContainSingle()
            .Which.actor.Should().Contain("security", "the bad-manifest event is journalled as a security log");
    }

    // FINDING 1 — an EXPIRED/absent entitlement (license lapsed/revoked → App stopped
    // refreshing the manifest) → the moat must not reapply. The gate fails closed before the
    // store is even loaded; the reconciler is never called; a SECURITY diagnostic is logged.
    [Fact]
    public async Task RunTick_WhenEntitlementLapsed_DoesNotLoadOrReconcile_LogsSecurity_FailsClosed()
    {
        var store = Substitute.For<IDesiredStateStore>();
        var reconciler = Substitute.For<IReconciler>();
        var diagnostics = new RecordingDiagnostics();
        var service = new ReconcileTickService(
            store, reconciler, diagnostics, new FakeEntitlementGate { Entitled = false });

        var result = await service.RunTickAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse("an expired moat entitlement must fail closed (no reapply)");
        result.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);

        // The unentitled moat never even reads the manifest, and never reaches the reconciler.
        await store.DidNotReceive().LoadAsync();
        await reconciler.DidNotReceive()
            .ReconcileAsync(Arg.Any<DesiredState>(), Arg.Any<CancellationToken>());

        diagnostics.Entries.Should().ContainSingle()
            .Which.Should().Match<(string tweakId, string from, string to, string actor)>(
                e => e.to == "reconcile-skipped-entitlement" && e.actor.Contains("security"));
    }
}
