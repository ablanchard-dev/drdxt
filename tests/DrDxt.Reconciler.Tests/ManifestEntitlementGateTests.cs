using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Le gate de reapplication — DrDXT GRATUIT (23/09/2026).
//
// Il n'y a plus de licence a verifier. Ce que les tests prouvent :
//   * rien d'arme (aucun manifeste) → Ok, no-op ;
//   * quelque chose d'arme et verifiable → Ok : ce que la personne a choisi est reapplique ;
//   * manifeste altere / non verifiable → fail-CLOSED : on ne reapplique jamais un etat qu'on ne
//     sait pas relire.
// -----------------------------------------------------------------------
public sealed class ManifestEntitlementGateTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static FakePolicy RegistryPolicy() => new FakePolicy()
        .Map("reflex.lowlatency", ReconcileSurface.Registry)
        .Map("power.plan", ReconcileSurface.PowerPlan);

    private static DesiredState TwoTweaks() => new(new[]
    {
        new AppliedTweak("reflex.lowlatency", "{\"v\":2}", "{\"v\":0}", At),
        new AppliedTweak("power.plan", "\"high\"", "\"balanced\"", At),
    });

    private static (SignedDesiredStateStore store, InMemoryManifestStorage storage, FakeKeyPair keys, FakeClock clock)
        Build()
    {
        var keys = new FakeKeyPair();
        var storage = new InMemoryManifestStorage();
        var clock = new FakeClock { UtcNow = At };
        var store = new SignedDesiredStateStore(storage, keys.Signer(), keys.Verifier(), RegistryPolicy(), clock);
        return (store, storage, keys, clock);
    }

    // Quelque chose d'arme et verifiable → reapplique.
    [Fact]
    public async Task ArmeEtVerifiable_EstAutorise()
    {
        var (store, _, _, _) = Build();
        (await store.CommitAsync(TwoTweaks())).IsOk.Should().BeTrue();

        (await new ManifestEntitlementGate(store).EnsureReapplyEntitledAsync(CancellationToken.None))
            .IsOk.Should().BeTrue("DrDXT est gratuit : ce que la personne a choisi se reapplique");
    }

    // Le manifeste ne porte aucun champ de licence : ce test fige la forme signee.
    [Fact]
    public async Task LeManifesteSigneNeComportePlusAucunChampDeLicence()
    {
        var (store, storage, _, _) = Build();
        (await store.CommitAsync(TwoTweaks())).IsOk.Should().BeTrue();

        var canonique = storage.Slot!.CanonicalJson;
        canonique.Should().NotContain("ExpiresAt");
        canonique.Should().NotContain("expiresAt");
        canonique.Should().NotContain("Entitlement");
    }

    // Aucun manifeste → Ok no-op : rien n'est arme.
    [Fact]
    public async Task PasDeManifeste_EstUnNoOpAutorise()
    {
        var (store, _, _, _) = Build();

        (await new ManifestEntitlementGate(store).EnsureReapplyEntitledAsync(CancellationToken.None))
            .IsOk.Should().BeTrue("rien d'arme : rien a reappliquer, rien a refuser");
    }

    // Manifeste non verifiable / altere → fail-CLOSED.
    [Fact]
    public async Task ManifesteNonVerifiable_EchoueFerme()
    {
        var (store, storage, _, _) = Build();
        (await store.CommitAsync(TwoTweaks())).IsOk.Should().BeTrue();

        var original = storage.Slot!;
        var chars = original.CanonicalJson.ToCharArray();
        chars[chars.Length / 2] = chars[chars.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = original with { CanonicalJson = new string(chars) };

        var gate = new ManifestEntitlementGate(store);
        var decision = await gate.EnsureReapplyEntitledAsync(CancellationToken.None);

        decision.IsOk.Should().BeFalse("un manifeste non verifiable ne pilote jamais le moat");
        decision.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }

    // LoadManifestAsync sans fichier → Ok(null) (premiere installation), pas un echec.
    [Fact]
    public async Task LoadManifestAsync_NoFile_ReturnsOkNull()
    {
        var (store, _, _, _) = Build();
        var loaded = await store.LoadManifestAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value.Should().BeNull();
    }
}
