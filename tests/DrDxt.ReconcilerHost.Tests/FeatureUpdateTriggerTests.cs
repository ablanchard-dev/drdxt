using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Le déclencheur composé de mise à jour de fonctionnalité.
//
// Le détecteur de build est le signal principal, fiable. La lecture d'événement IUpdateSignal
// (fin de Windows Update ou de Setup dans le journal d'événements) est un signal de
// corroboration qui peut jouer plus tôt que le basculement du build (setup fini, le build
// basculera au prochain démarrage). Le déclencheur joue quand l'un des deux dit « mise à jour » :
// principal ou corroboration. Un Fail du principal n'est pas masqué par la corroboration : un
// build illisible remonte en Fail (l'hôte ne doit pas traiter l'inconnu comme « mis à jour » en
// silence) ; la lecture de corroboration est au mieux et ne transforme jamais un Fail principal
// en Ok.
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
        // Le principal prouve déjà une mise à jour ; l'indisponibilité de la lecture de corroboration
        // ne doit pas rétrograder une mise à jour prouvée. (La corroboration est additive, au mieux.)
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

/// <summary>Faux signal de corroboration de mise à jour. <see cref="Recent"/> est ce qu'il rapporte ;
/// <see cref="Fail"/> fait échouer la lecture (chemin au mieux).</summary>
public sealed class FakeUpdateSignal : IUpdateSignal
{
    public bool Recent { get; set; }
    public bool Fail { get; set; }

    public Task<Result<bool>> RecentSetupCompletionAsync(CancellationToken ct)
        => Task.FromResult(Fail
            ? Result<bool>.Fail(ErrorCodes.IoFailure, "event log read failed (test)")
            : Result<bool>.Ok(Recent));
}
