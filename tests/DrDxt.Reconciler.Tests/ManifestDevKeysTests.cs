using System.Text;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using DrDxt.TestSupport;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// The V1 EMBEDDED manifest keypair (ManifestDevKeys) must be a real matched
// Ed25519 pair: a manifest signed by the embedded DEV PRIVATE seed must VERIFY
// under the embedded PUBLIC verifier (so the team build verifies real manifests,
// not just fail-closed), and a manifest signed by ANY OTHER key must fail closed.
//
// This exercises the real Ed25519 (NSec) sign/verify — not the in-memory fake —
// end-to-end through the SignedDesiredStateStore, proving the embedded pair is the
// trust root the App (sign) and the host/App (verify) actually share.
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

    // The embedded public key is the canonical 32-byte Ed25519 length.
    [Fact]
    public void EmbeddedKeys_AreEd25519RawLength()
    {
        ManifestDevKeys.PublicKey.Should().HaveCount(32);
        ManifestDevTestKeys.DevPrivateSeed.Should().HaveCount(32);
    }

    // The embedded PRIVATE seed and PUBLIC key are a MATCHED pair: sign with the dev
    // signer, verify with the embedded verifier → true.
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

    // End-to-end through the real store: the App-style signer commits, the embedded
    // verifier loads it back. Proves the host/App verify path (embedded key) accepts a
    // manifest the App actually produced.
    [Fact]
    public async Task SignWithDevSeed_ThenLoadWithEmbeddedVerifier_RoundTrips()
    {
        var storage = new InMemoryManifestStorage();
        var clock = new FakeClock { UtcNow = At };

        var committing = new SignedDesiredStateStore(
            storage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
            ManifestDevKeys.CreateVerifier().Value!, ReconcilablePolicy(), clock);
        (await committing.CommitAsync(OneTweak())).IsOk.Should().BeTrue();

        // A SEPARATE verify-only store (signer:null) that holds only the embedded public
        // key — exactly how the host is wired.
        var verifyOnly = new SignedDesiredStateStore(
            storage, signer: null, ManifestDevKeys.CreateVerifier().Value!,
            ReconcilablePolicy(), clock);

        var loaded = await verifyOnly.LoadAsync();
        loaded.IsOk.Should().BeTrue();
        loaded.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("reflex.lowlatency");
    }

    // A manifest signed by a DIFFERENT (foreign) key must fail closed under the embedded
    // verifier — the owner cannot forge with their own keypair.
    [Fact]
    public async Task ForeignKeySignedManifest_FailsClosed_UnderEmbeddedVerifier()
    {
        var storage = new InMemoryManifestStorage();
        var clock = new FakeClock { UtcNow = At };

        // Commit with a foreign keypair (not the embedded one).
        var foreignSeed = new byte[32];
        for (var i = 0; i < foreignSeed.Length; i++) foreignSeed[i] = (byte)(i + 1);
        var foreignSigner = Ed25519ManifestSigner.FromSeed(foreignSeed);
        var foreignVerifier = new Ed25519ManifestVerifier(
            DeriveForeignPublicKey(foreignSeed));

        var foreignStore = new SignedDesiredStateStore(
            storage, foreignSigner, foreignVerifier, ReconcilablePolicy(), clock);
        (await foreignStore.CommitAsync(OneTweak())).IsOk.Should().BeTrue();

        // Now load with the EMBEDDED verifier → must reject (fail-closed).
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
        // Use NSec via the signer's own key to export the matching public key.
        var key = NSec.Cryptography.Key.Import(
            NSec.Cryptography.SignatureAlgorithm.Ed25519, seed,
            NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        return key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
    }
}
