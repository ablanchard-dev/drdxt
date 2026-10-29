using DrDxt.Core;
using NSec.Cryptography;

namespace DrDxt.Reconciler.Manifest;

// Signature / vérification Ed25519 derrière IManifestSigner / IManifestVerifier, par NSec / libsodium (Ed25519 n'est pas
// dans la BCL .NET). L'asymétrie fait tout : un Verifier, construit sur la seule clé publique, ne fait que vérifier ;
// l'hôte réconciliateur n'en reçoit qu'un, jamais la clé privée. D'où le refus d'un MAC symétrique / DPAPI : le
// propriétaire pourrait redériver la clé.

/// <summary>Vérificateur Ed25519 : ne tient que la clé publique de 32 octets. Verify est total :
/// toute signature malformée ou de mauvaise longueur rend false (fail-closed), jamais d'exception.</summary>
public sealed class Ed25519ManifestVerifier : IManifestVerifier
{
    private readonly PublicKey _publicKey;

    /// <summary>Construit un vérificateur à partir d'une clé publique Ed25519 de 32 octets.</summary>
    public Ed25519ManifestVerifier(ReadOnlySpan<byte> publicKey)
        => _publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);

    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature)
    {
        // Le Verify de NSec rend déjà false sur une signature mauvaise ou courte ; garder aussi la
        // mauvaise longueur évidente pour ne jamais passer à la bibliothèque de quoi lever.
        if (signature.Length != SignatureAlgorithm.Ed25519.SignatureSize)
            return false;
        return SignatureAlgorithm.Ed25519.Verify(_publicKey, canonicalBytes, signature);
    }
}

/// <summary>Signataire Ed25519 : tient la clé privée. Ne vit que du côté du commit ; jamais
/// injecté dans l'hôte de réconciliation / de vérification (hypothèse de garde de la clé : voir
/// SignedDesiredStateStore). Sign échoue proprement sur toute erreur cryptographique.</summary>
public sealed class Ed25519ManifestSigner : IManifestSigner
{
    private readonly Key _key;

    /// <summary>Emballe une clé Ed25519 NSec déjà importée (l'appelant possède sa durée de vie et
    /// son stockage sûr). Dispose est à la charge de l'appelant.</summary>
    public Ed25519ManifestSigner(Key key) => _key = key;

    /// <summary>Importe un signataire depuis une graine brute de clé privée Ed25519 de 32 octets.</summary>
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
            // Frontière cryptographique emballée : échec propre, jamais de cascade.
            return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid, "Ed25519 signing failed.");
        }
    }
}
