using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// FeatureUpdateDetector.WasFeatureUpdateInstalledAsync :
//   lire le build courant (IOsBuildProbe) et le comparer à ReconcilerState.LastFeatureUpdateBuild.
//   différent → Ok(true) et persister le nouveau LastFeatureUpdateBuild (pour que le déclencheur
//   joue une fois par mise à jour de fonctionnalité, pas à chaque tic).
//   identique → Ok(false), aucune écriture.
//   sonde ou magasin en Fail → Fail (rien de deviné).
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
        // Première installation : LastFeatureUpdateBuild est vide. Un build courant non vide est
        // « nouveau » : l'enregistrer pour que le tic suivant avec le même build ne fasse rien.
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
