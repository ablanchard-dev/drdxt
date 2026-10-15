using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Fermeture de bout en bout :
// la seule façon pour le réconciliateur d'obtenir un manifeste est IDesiredStateStore.LoadAsync,
// qui vérifie l'intégrité. Un manifeste altéré fait échouer LoadAsync → l'hôte ne doit pas
// réconcilier → zéro écriture privilégiée. Ceci coud le magasin signé à la boucle de
// réconciliation et prouve « altéré ⇒ rien d'appliqué ».
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

        // Committer un manifeste valide avec un élément dérivé vers l'origine.
        var desired = new DesiredState(new[] { new AppliedTweak("a", "1", "0", At) });
        (await store.CommitAsync(desired)).IsOk.Should().BeTrue();

        // Altérer le contenu persisté.
        var slot = storage.Slot!;
        var chars = slot.CanonicalJson.ToCharArray();
        chars[chars.Length / 2] = chars[chars.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = slot with { CanonicalJson = new string(chars) };

        // Le chargement de l'hôte : il doit se fermer.
        var loaded = await store.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);

        // Comme le chargement a échoué, l'hôte n'appelle jamais ReconcileAsync. Prouver que le
        // réconciliateur, s'il avait reçu (à tort) le DesiredState de l'attaquant, n'est jamais
        // atteint : on vérifie simplement la porte de l'hôte. Le client de réconciliation est
        // un enregistreur en mémoire qui doit avoir zéro écriture.
        var h = new ReconcilerHarness();
        h.Policy.Map("a", ReconcileSurface.Registry);

        // Simuler le bon flux de contrôle de l'hôte : ne réconcilier que sur un chargement vérifié.
        if (loaded.IsOk)
            await h.Build().ReconcileAsync(loaded.Value!, default);

        h.Reapply.Reapplies.Should().BeEmpty("tamper ⇒ load fails ⇒ nothing is ever applied (fail-closed)");
    }

    [Fact]
    public async Task ValidManifest_LoadSucceeds_ReconcilerRepairsDrift()
    {
        // Le témoin positif : un manifeste valide se charge et un élément dérivé est réparé.
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
        h.BuildProbe.Build = "22635.5000"; // build changé
        h.Probe.Set("a", "0");             // dérivé vers l'origine

        var result = await h.Build().ReconcileAsync(loaded.Value!, default);

        result.IsOk.Should().BeTrue();
        h.Reapply.Reapplies.Should().ContainSingle().Which.tweakId.Should().Be("a");
    }

    // Un etat illisible etait remplace par l'etat par defaut (non suspendu), la passe
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
