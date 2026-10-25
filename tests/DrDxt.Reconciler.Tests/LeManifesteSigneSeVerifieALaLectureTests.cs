using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

/// <summary>
/// Le service relit le manifeste signé par le magasin, une fois par passage. Ce que ces tests tiennent :
/// rien d'armé (aucun manifeste) → état vide ; armé et vérifiable → l'état choisi ; manifeste altéré → refus
/// (fail-closed) : on ne réapplique jamais un état qu'on ne sait pas relire. Le manifeste ne porte aucun champ de licence.
/// </summary>
public sealed class LeManifesteSigneSeVerifieALaLectureTests
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

        var lu = await store.LoadAsync();
        lu.IsOk.Should().BeTrue("ce que la personne a choisi se reapplique");
        lu.Value!.Tweaks.Should().HaveCount(2);
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

        var lu = await store.LoadAsync();
        lu.IsOk.Should().BeTrue("rien d'arme : rien a reappliquer, rien a refuser");
        lu.Value!.Tweaks.Should().BeEmpty();
    }

    // Manifeste non verifiable / altere → fail-CLOSED.
    [Fact]
    public async Task ManifesteNonVerifiable_EchoueFerme()
    {
        var (store, storage, _, _) = Build();
        (await store.CommitAsync(TwoTweaks())).IsOk.Should().BeTrue();

        var original = storage.Slot!;
        // Une date changée garde un JSON valide : seule la signature peut refuser. Un caractère au hasard cassait le JSON,
        // et le refus pouvait venir de la lecture sans que la signature soit vérifiée.
        var altere = original.CanonicalJson.Replace("2026", "2027", StringComparison.Ordinal);
        altere.Should().NotBe(original.CanonicalJson);
        System.Text.Json.JsonDocument.Parse(altere).Dispose();
        storage.Slot = original with { CanonicalJson = altere };

        var decision = await store.LoadAsync();

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
