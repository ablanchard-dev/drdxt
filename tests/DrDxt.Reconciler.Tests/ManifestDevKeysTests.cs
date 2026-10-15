using System.Text;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using DrDxt.TestSupport;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// La paire de clés de manifeste embarquée V1 (ManifestDevKeys) doit être une vraie paire
// Ed25519 assortie : un manifeste signé par la graine privée de dev embarquée doit se vérifier
// sous le vérificateur public embarqué (pour que la version d'équipe vérifie de vrais
// manifestes, pas seulement se ferme), et un manifeste signé par toute autre clé doit se fermer.
//
// Ceci exerce le vrai Ed25519 (NSec), signature et vérification, pas le faux en mémoire,
// de bout en bout à travers le SignedDesiredStateStore : preuve que la paire embarquée est
// la racine de confiance que l'App (signe) et l'hôte/l'App (vérifient) partagent vraiment.
// -----------------------------------------------------------------------
public sealed class ManifestDevKeysTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    private static FakePolicy ReconcilablePolicy() => new FakePolicy()
        .Map("reflex.lowlatency", ReconcileSurface.Registry);

    private static DesiredState OneTweak() => new(new[]
    {
        new AppliedTweak("reflex.lowlatency", "2", "0", At),
    });

    // La clé publique embarquée a la longueur canonique Ed25519 de 32 octets.
    [Fact]
    public void EmbeddedKeys_AreEd25519RawLength()
    {
        ManifestDevKeys.PublicKey.Should().HaveCount(32);
        ManifestDevTestKeys.DevPrivateSeed.Should().HaveCount(32);
    }

    // La graine privée embarquée et la clé publique sont une paire assortie : signer avec le
    // signataire de dev, vérifier avec le vérificateur embarqué → vrai.
    [Fact]
    public void DevSigner_AndEmbeddedVerifier_AreAMatchedPair()
    {
        var signer = Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed);
        var verifier = ManifestDevKeys.CreateVerifier().Value!;

        var payload = Encoding.UTF8.GetBytes("the exact canonical bytes");
        var sig = signer.Sign(payload);
        sig.IsOk.Should().BeTrue();

        verifier.Verify(payload, sig.Value!).Should().BeTrue();
    }

    // De bout en bout à travers le vrai magasin : le signataire façon App committe, le
    // vérificateur embarqué le recharge. Prouve que le chemin de vérification hôte/App (clé
    // embarquée) accepte un manifeste que l'App a réellement produit.
    [Fact]
    public async Task SignWithDevSeed_ThenLoadWithEmbeddedVerifier_RoundTrips()
    {
        var storage = new InMemoryManifestStorage();
        var clock = new FakeClock { UtcNow = At };

        var committing = new SignedDesiredStateStore(
            storage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
            ManifestDevKeys.CreateVerifier().Value!, ReconcilablePolicy(), clock);
        (await committing.CommitAsync(OneTweak())).IsOk.Should().BeTrue();

        // Un magasin séparé, vérification seule (signer:null), qui ne tient que la clé publique
        // embarquée : exactement le câblage de l'hôte.
        var verifyOnly = new SignedDesiredStateStore(
            storage, signer: null, ManifestDevKeys.CreateVerifier().Value!,
            ReconcilablePolicy(), clock);

        var loaded = await verifyOnly.LoadAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("reflex.lowlatency");
    }

    // Un manifeste signé par une autre clé (étrangère) doit se fermer sous le vérificateur
    // embarqué : le propriétaire ne peut pas forger avec sa propre paire.
    [Fact]
    public async Task ForeignKeySignedManifest_FailsClosed_UnderEmbeddedVerifier()
    {
        var storage = new InMemoryManifestStorage();
        var clock = new FakeClock { UtcNow = At };

        // Committer avec une paire étrangère (pas celle embarquée).
        var foreignSeed = new byte[32];
        for (var i = 0; i < foreignSeed.Length; i++) foreignSeed[i] = (byte)(i + 1);
        var foreignSigner = Ed25519ManifestSigner.FromSeed(foreignSeed);
        var foreignVerifier = new Ed25519ManifestVerifier(
            DeriveForeignPublicKey(foreignSeed));

        var foreignStore = new SignedDesiredStateStore(
            storage, foreignSigner, foreignVerifier, ReconcilablePolicy(), clock);
        (await foreignStore.CommitAsync(OneTweak())).IsOk.Should().BeTrue();

        // Charger maintenant avec le vérificateur embarqué → doit rejeter (fermé).
        var verifyOnly = new SignedDesiredStateStore(
            storage, signer: null, ManifestDevKeys.CreateVerifier().Value!,
            ReconcilablePolicy(), clock);

        var loaded = await verifyOnly.LoadAsync();
        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
        loaded.Value.Should().BeNull();
    }

    private static byte[] DeriveForeignPublicKey(byte[] seed)
    {
        // Utiliser NSec via la clé du signataire pour exporter la clé publique correspondante.
        var key = NSec.Cryptography.Key.Import(
            NSec.Cryptography.SignatureAlgorithm.Ed25519, seed,
            NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        return key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
    }
}
