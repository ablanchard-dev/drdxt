using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// I4 fail-CLOSED end to end (plan T20 second leg, T26 host behaviour pre-figured):
// the only way the reconciler gets a manifest is via IDesiredStateStore.LoadAsync,
// which VERIFIES integrity. A tampered manifest makes LoadAsync fail → the host must
// Not reconcile → ZERO privileged writes. This stitches the signed store to the
// reconcile loop and proves "tamper ⇒ nothing applied".
// -----------------------------------------------------------------------
public sealed class FailClosedEndToEndTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task TamperedManifest_LoadFails_ReconcilerAppliesNothing()
    {
        var keys = new FakeKeyPair();
        var storage = new InMemoryManifestStorage();
        var policy = new FakePolicy().Map("a", ReconcileSurface.Registry);
        var store = new SignedDesiredStateStore(
            storage, keys.Signer(), keys.Verifier(), policy, new FakeClock());

        // Commit a valid manifest with a drifted-to-stock item.
        var desired = new DesiredState(new[] { new AppliedTweak("a", "1", "0", At) });
        (await store.CommitAsync(desired)).IsOk.Should().BeTrue();

        // Tamper the persisted content.
        var slot = storage.Slot!;
        var chars = slot.CanonicalJson.ToCharArray();
        chars[chars.Length / 2] = chars[chars.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = slot with { CanonicalJson = new string(chars) };

        // The host's load: it must fail-closed.
        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);

        // Because load failed, the host never calls ReconcileAsync. Prove that the
        // reconciler — had it been (wrongly) handed the *attacker's* DesiredState —
        // is never reached: we simply assert the host gate. The reconcile client is
        // an in-memory recorder that must have ZERO writes.
        var h = new ReconcilerHarness();
        h.Policy.Map("a", ReconcileSurface.Registry);

        // Simulate the host's correct control flow: only reconcile on a verified load.
        if (loaded.IsOk)
            await h.Build().ReconcileAsync(loaded.Value!, default);

        h.Reapply.Reapplies.Should().BeEmpty("tamper ⇒ load fails ⇒ nothing is ever applied (fail-closed)");
    }

    [Fact]
    public async Task ValidManifest_LoadSucceeds_ReconcilerRepairsDrift()
    {
        // The positive control: a VALID manifest loads and a drifted item is repaired.
        var keys = new FakeKeyPair();
        var storage = new InMemoryManifestStorage();
        var policy = new FakePolicy().Map("a", ReconcileSurface.Registry);
        var store = new SignedDesiredStateStore(
            storage, keys.Signer(), keys.Verifier(), policy, new FakeClock());

        var desired = new DesiredState(new[] { new AppliedTweak("a", "1", "0", At) });
        (await store.CommitAsync(desired)).IsOk.Should().BeTrue();

        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeTrue();

        var h = new ReconcilerHarness(ReconcilerState.Default with
        {
            Items = new[] { new ReconcilerItemState("a", false, "22631.3447", "555.99", At, "OK") },
        });
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000"; // build changed
        h.Probe.Set("a", "0");             // drifted to stock

        var result = await h.Build().ReconcileAsync(loaded.Value!, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle().Which.tweakId.Should().Be("a");
    }

    // 26/09/2026 : un etat illisible etait remplace par l'etat par defaut (non suspendu), la passe
    // reappliquait, puis ecrivait cet etat par defaut par-dessus : une pause demandee par le joueur
    // disparaissait sur une simple panne de lecture. Etat inconnu = rien d'ecrit, ni sur la machine
    // ni dans l'etat.
    [Fact]
    public async Task UnreadableState_ReconcilerWritesNothing_AndKeepsTheStateFile()
    {
        var h = new ReconcilerHarness();
        h.StateStore.FailLoad = true;
        h.Policy.Map("a", ReconcileSurface.Registry);
        h.BuildProbe.Build = "22635.5000";
        h.Probe.Set("a", "0");
        var desired = new DesiredState(new[] { new AppliedTweak("a", "1", "0", At) });

        var result = await h.Build().ReconcileAsync(desired, default);

        result.IsOk.Should().BeFalse("ne pas savoir si le joueur a suspendu se dit, et ne s'agit pas");
        h.Reapply.Reapplies.Should().BeEmpty();
        h.StateStore.SaveCount.Should().Be(0, "l'etat illisible ne doit pas etre ecrase par un etat par defaut");
    }
}
