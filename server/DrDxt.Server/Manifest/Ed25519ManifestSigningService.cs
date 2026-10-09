using DrDxt.Server.Common;
using NSec.Cryptography;

namespace DrDxt.Server.Manifest;

// Ed25519 manifest signer (NSec / libsodium) — the SIGN side that mirrors the client's
// Ed25519ManifestVerifier on the VERIFY side. The 32-byte raw PRIVATE seed is read from
// configuration/env at construction (ManifestSigningOptions); it is NEVER hardcoded and NEVER
// ships in a client/service binary. A blank/malformed/wrong-length seed = FAIL-CLOSED: every
// Sign returns Fail (we never throw at construction, never fail-open at startup).
public sealed class Ed25519ManifestSigningService : IManifestSigningService
{
    private const int Ed25519SeedLength = 32;

    // Imported once from config. Null ⇒ no usable key ⇒ fail-closed on every Sign.
    private readonly Key? _key;

    public Ed25519ManifestSigningService(ManifestSigningOptions options)
        => _key = TryImport(options?.Ed25519PrivateSeedBase64);

    public Result<string> Sign(byte[] canonicalBytes)
    {
        if (_key is null)
        {
            return Result<string>.Fail(
                ServerErrorCodes.ManifestSigningUnconfigured,
                "No Ed25519 manifest-signing seed is configured (ManifestSigning__Ed25519PrivateSeedBase64); refusing to sign.");
        }

        if (canonicalBytes is null || canonicalBytes.Length == 0)
        {
            return Result<string>.Fail(ServerErrorCodes.ManifestSigningFailed, "Nothing to sign.");
        }

        try
        {
            var signature = SignatureAlgorithm.Ed25519.Sign(_key, canonicalBytes);
            return Result<string>.Ok(Convert.ToBase64String(signature));
        }
        catch (Exception)
        {
            // Crypto frontier wrapped — fail-clean, never cascade, never fail-open.
            return Result<string>.Fail(ServerErrorCodes.ManifestSigningFailed, "Ed25519 signing failed.");
        }
    }

    // Import the seed from base64 config. Any problem (absent / not base64 / wrong length /
    // import error) ⇒ null, so the service is fail-closed rather than throwing at startup.
    private static Key? TryImport(string? base64Seed)
    {
        if (string.IsNullOrWhiteSpace(base64Seed))
        {
            return null;
        }

        try
        {
            var seed = Convert.FromBase64String(base64Seed.Trim());
            if (seed.Length != Ed25519SeedLength)
            {
                return null;
            }

            return Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
