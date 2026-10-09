using DrDxt.Core;
using NSec.Cryptography;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// Ed25519 sign/verify behind the IManifestSigner / IManifestVerifier seams
// (master plan §11: "Crypto Ed25519 = NSec"). Ed25519 is not in the .NET BCL, so
// the project's tranched lib (NSec / libsodium) provides it.
//
// ASYMMETRY is the whole point (master §9.12 / B1): a Verifier is constructed from
// the PUBLIC key alone and can only verify; a Signer holds the PRIVATE key and can
// Only sign. The reconciler/verify host is given a Verifier — it cannot forge,
// because it does not have (and is never injected with) the private key. This is
// exactly why a symmetric MAC / DPAPI is refused: the owner could re-derive the key.
// -----------------------------------------------------------------------

/// <summary>Ed25519 verifier — holds the 32-byte PUBLIC key only. Verify is total:
/// any malformed signature / wrong length returns false (fail-closed), never throws.</summary>
public sealed class Ed25519ManifestVerifier : IManifestVerifier
{
    private readonly PublicKey _publicKey;

    /// <summary>Constructs a verifier from a 32-byte Ed25519 public key.</summary>
    public Ed25519ManifestVerifier(ReadOnlySpan<byte> publicKey)
        => _publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);

    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature)
    {
        // NSec's Verify already returns false on a bad/short signature; guard the
        // obvious wrong length too so we never hand the lib something it might throw on.
        if (signature.Length != SignatureAlgorithm.Ed25519.SignatureSize)
            return false;
        return SignatureAlgorithm.Ed25519.Verify(_publicKey, canonicalBytes, signature);
    }
}

/// <summary>Ed25519 signer — holds the PRIVATE key. Lives only on the commit side;
/// never injected into the reconcile/verify host (key-custody assumption — see
/// SignedDesiredStateStore). Sign is fail-clean on any crypto error.</summary>
public sealed class Ed25519ManifestSigner : IManifestSigner
{
    private readonly Key _key;

    /// <summary>Wraps an already-imported NSec Ed25519 key (the caller owns its
    /// lifetime / secure storage). Dispose is the caller's responsibility.</summary>
    public Ed25519ManifestSigner(Key key) => _key = key;

    /// <summary>Imports a signer from a 32-byte raw Ed25519 private-key seed.</summary>
    public static Ed25519ManifestSigner FromSeed(ReadOnlySpan<byte> seed)
    {
        var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        return new Ed25519ManifestSigner(key);
    }

    public Result<byte[]> Sign(ReadOnlySpan<byte> canonicalBytes)
    {
        try
        {
            return Result<byte[]>.Ok(SignatureAlgorithm.Ed25519.Sign(_key, canonicalBytes));
        }
        catch (Exception)
        {
            // Crypto frontier wrapped — fail-clean, never cascade.
            return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid, "Ed25519 signing failed.");
        }
    }
}
