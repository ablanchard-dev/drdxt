using System.Text;
using DrDxt.Server.Common;
using DrDxt.Server.Manifest;
using FluentAssertions;
using NSec.Cryptography;

namespace DrDxt.Server.Tests;

// Server-side manifest signing CUSTODY: the PRIVATE seed lives ONLY here (from config/env), and
// the signature it produces verifies under the matching PUBLIC key the client embeds. Fail-closed
// when no seed is configured (we never sign with an absent/malformed key, never fail-open).
public sealed class Ed25519ManifestSigningServiceTests
{
    // A fixed seed for the test fixture ONLY. In production this arrives via env (the test seed is
    // never committed as a real signing key). 32 bytes = canonical Ed25519 raw private seed length.
    private static byte[] Seed()
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = (byte)(i + 1);
        }

        return s;
    }

    private static Ed25519ManifestSigningService Configured()
        => new(new ManifestSigningOptions { Ed25519PrivateSeedBase64 = Convert.ToBase64String(Seed()) });

    [Fact]
    public void Sign_WithConfiguredSeed_ProducesSignatureTheMatchingPublicKeyVerifies()
    {
        var canonical = Encoding.UTF8.GetBytes("the exact canonical manifest bytes");

        var result = Configured().Sign(canonical);

        result.IsOk.Should().BeTrue();
        var signature = Convert.FromBase64String(result.Value!);

        // The client holds ONLY the PUBLIC key derived from the same seed — prove it VERIFIES the
        // server's signature (the asymmetric custody contract: server signs, client verifies).
        using var key = Key.Import(SignatureAlgorithm.Ed25519, Seed(), KeyBlobFormat.RawPrivateKey);
        SignatureAlgorithm.Ed25519.Verify(key.PublicKey, canonical, signature).Should().BeTrue();
    }

    [Fact]
    public void Sign_IsDeterministic_OverIdenticalBytes()
    {
        var canonical = Encoding.UTF8.GetBytes("payload");

        var a = Configured().Sign(canonical);
        var b = Configured().Sign(canonical);

        a.IsOk.Should().BeTrue();
        b.IsOk.Should().BeTrue();
        a.Value.Should().Be(b.Value, "Ed25519 is deterministic over identical bytes + key");
    }

    [Fact]
    public void Sign_WhenNoSeedConfigured_FailsClosed()
    {
        var service = new Ed25519ManifestSigningService(new ManifestSigningOptions());

        var result = service.Sign(Encoding.UTF8.GetBytes("payload"));

        result.IsOk.Should().BeFalse("an unconfigured signer must refuse to sign (fail-closed)");
        result.Error!.Value.Code.Should().Be(ServerErrorCodes.ManifestSigningUnconfigured);
    }

    [Fact]
    public void Sign_WhenSeedMalformed_FailsClosed()
    {
        // Not base64.
        new Ed25519ManifestSigningService(
                new ManifestSigningOptions { Ed25519PrivateSeedBase64 = "not-base64-$$$" })
            .Sign(Encoding.UTF8.GetBytes("payload")).IsOk.Should().BeFalse();

        // Valid base64 but wrong length (16 bytes, not 32).
        new Ed25519ManifestSigningService(
                new ManifestSigningOptions { Ed25519PrivateSeedBase64 = Convert.ToBase64String(new byte[16]) })
            .Sign(Encoding.UTF8.GetBytes("payload")).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Sign_WhenNothingToSign_FailsClean()
    {
        var result = Configured().Sign(Array.Empty<byte>());

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ServerErrorCodes.ManifestSigningFailed);
    }
}
