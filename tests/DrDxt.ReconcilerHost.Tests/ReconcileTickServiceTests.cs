using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// ReconcileTickService.RunTickAsync :
//   * porte de droit d'abord : un droit absent ou expiré → ni chargement ni réconciliation,
//     un diagnostic de sécurité journalisé, rendre le Fail (douve fermée).
//   * charger via IDesiredStateStore.LoadAsync()
//   * chargement en Fail (altération, schéma, E/S) → ne pas réconcilier, journaliser un diagnostic
//     de sécurité, rendre le Fail (échec propre : jamais entrer en boucle d'écriture sur un mauvais manifeste)
//   * chargement Ok → appeler IReconciler.ReconcileAsync(state, ct) et rendre son résultat.
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
        var service = new ReconcileTickService(store, reconciler, diagnostics);

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

        var service = new ReconcileTickService(store, reconciler, new RecordingDiagnostics());

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
        var service = new ReconcileTickService(store, reconciler, diagnostics);

        var result = await service.RunTickAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);

        // Un mauvais manifeste n'atteint jamais le réconciliateur (pas de boucle d'écriture).
        await reconciler.DidNotReceive()
            .ReconcileAsync(Arg.Any<DesiredState>(), Arg.Any<CancellationToken>());

        // Un diagnostic de sécurité a été journalisé (l'acteur le marque comme événement de sécurité).
        diagnostics.Entries.Should().ContainSingle()
            .Which.actor.Should().Contain("security", "the bad-manifest event is journalled as a security log");
    }
}
