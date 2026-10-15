using System.Text;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;
using NSec.Cryptography;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Le vrai schéma Ed25519 derrière les coutures : prouve la propriété de sécurité
// asymétrique de bout en bout (signer avec la privée, vérifier avec la publique ; altérer → faux).
//
// C'est la cryptographie de production (NSec / libsodium). Seul test qui touche la
// bibliothèque native ; tous les tests de logique du réconciliateur utilisent le FakeKeyPair en mémoire.
// -----------------------------------------------------------------------
public sealed class Ed25519SignatureSchemeTests
{
    private static (Ed25519ManifestSigner signer, Ed25519ManifestVerifier verifier) NewPair()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var seed = key.Export(KeyBlobFormat.RawPrivateKey);
        var pub = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (Ed25519ManifestSigner.FromSeed(seed), new Ed25519ManifestVerifier(pub));
    }

    [Fact]
    public void Sign_thenVerify_withMatchingPublicKey_succeeds()
    {
        var (signer, verifier) = NewPair();
        var bytes = Encoding.UTF8.GetBytes("the canonical manifest content");

        var sig = signer.Sign(bytes);
        sig.IsOk.Should().BeTrue();

        verifier.Verify(bytes, sig.Value!).Should().BeTrue();
    }

    [Fact]
    public void Verify_failsOnTamper_evenOneByte()
    {
        var (signer, verifier) = NewPair();
        var bytes = Encoding.UTF8.GetBytes("the canonical manifest content");
        var sig = signer.Sign(bytes).Value!;

        var tampered = (byte[])bytes.Clone();
        tampered[0] ^= 0x01;

        verifier.Verify(tampered, sig).Should().BeFalse("a tampered payload must fail verification");
    }

    [Fact]
    public void Verify_failsForForeignSignature_ownerCannotForge()
    {
        // Deux paires de clés indépendantes : un faussaire qui a sa propre clé privée ne peut pas
        // produire une signature que notre vérificateur à clé publique accepte.
        var (_, ourVerifier) = NewPair();
        var (forgerSigner, _) = NewPair();
        var bytes = Encoding.UTF8.GetBytes("attacker-chosen content");

        var forged = forgerSigner.Sign(bytes).Value!;
        ourVerifier.Verify(bytes, forged).Should().BeFalse("only the holder of OUR private key can produce a valid sig");
    }

    [Fact]
    public void Verify_failsOnMalformedSignature_failClosed_neverThrows()
    {
        var (_, verifier) = NewPair();
        var bytes = Encoding.UTF8.GetBytes("content");

        verifier.Verify(bytes, new byte[] { 1, 2, 3 }).Should().BeFalse("wrong-length signature fails closed");
        verifier.Verify(bytes, Array.Empty<byte>()).Should().BeFalse();
    }

    [Fact]
    public async Task SignedDesiredStateStore_withRealEd25519_roundTripsAndRejectsTamper()
    {
        // Le magasin piloté par la vraie cryptographie, de bout en bout.
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var seed = key.Export(KeyBlobFormat.RawPrivateKey);
        var pub = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var storage = new InMemoryManifestStorage();
        var policy = new FakePolicy().Map("reflex.lowlatency", ReconcileSurface.Registry);
        var store = new SignedDesiredStateStore(
            storage,
            Ed25519ManifestSigner.FromSeed(seed),
            new Ed25519ManifestVerifier(pub),
            policy,
            new FakeClock());

        var desired = new DesiredState(new[]
        {
            new AppliedTweak("reflex.lowlatency", "{\"v\":2}", "{\"v\":0}", new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero)),
        });

        (await store.CommitAsync(desired)).IsOk.Should().BeTrue();
        (await store.LoadAsync()).Value!.Tweaks.Should().ContainSingle();

        // Altérer → fermé sous le vrai Ed25519 (inverser un octet du contenu signé).
        var slot = storage.Slot!;
        var chars = slot.CanonicalJson.ToCharArray();
        chars[chars.Length / 2] = chars[chars.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = slot with { CanonicalJson = new string(chars) };
        var tampered = await store.LoadAsync();
        tampered.IsOk.Should().BeFalse();
        tampered.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }
}
