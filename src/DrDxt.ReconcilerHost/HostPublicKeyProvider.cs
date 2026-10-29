using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// HostPublicKeyProvider : la clé publique Ed25519 brute (32 octets) du manifeste, en vérification seule, lue sous la
// racine en écriture admin. L'hôte réconciliateur ne signe jamais ; la clé privée de la machine n'est lue que par le
// helper élevé (SignedDesiredStateStore), qui dérive la publique (MachineManifestKey) et l'écrit au premier
// CommitManifestOp. Aucune clé embarquée.
// Fail-closed : clé absente ou malformée → Fail, un vérificateur qui refuse tout, l'hôte ne réconcilie pas et ne se
// rabat jamais sur une lecture non vérifiée. Ce fournisseur ne produit qu'un vérificateur.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class HostPublicKeyProvider
{
    private const string FileName = "manifest-public.key";
    private const int Ed25519PublicKeyLength = 32;

    /// <summary>Le chemin par défaut de la clé publique, voisin du manifeste et du sidecar.</summary>
    public static string DefaultPath()
        => Path.Combine(ProgramDataManifestStorage.DefaultRoot(), FileName);

    /// <summary>Charge la clé publique Ed25519 brute de 32 octets. Absente, de mauvaise longueur ou non fiable au repos →
    /// Fail : pas de vérificateur, l'hôte ne réconcilie pas.</summary>
    /// <param name="enforceTrust">En production : propriétaire Administrateurs/SYSTEM et racine non inscriptible par un
    /// non-admin (<see cref="TrustedManifestRoot.EnsureTrustedFile"/>), pour rejeter une clé falsifiée dans une racine de
    /// l'utilisateur (BLOCKER #1a). False dans les tests, sur un fichier temporaire non admin.</param>
    public static Result<byte[]> Load(string path, bool enforceTrust = true)
    {
        try
        {
            if (!File.Exists(path))
                return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                    $"Manifest public key not provisioned at '{path}'.");

            // Une clé présente mais non fiable au repos est une attaque, pas une clé absente : refus ferme.
            if (enforceTrust)
            {
                var trust = TrustedManifestRoot.EnsureTrustedFile(path);
                if (!trust.IsOk)
                    return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                        $"Manifest public key is not admin-trusted at rest: {trust.Error!.Value.Message}");
            }

            var key = File.ReadAllBytes(path);
            if (key.Length != Ed25519PublicKeyLength)
                return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                    $"Manifest public key has wrong length ({key.Length}, expected {Ed25519PublicKeyLength}).");

            // Un fichier de cle qui contient la cle de dev est refuse, dans toutes les
            // configurations : sa graine privee vit dans les tests du depot.
            if (ManifestDevKeys.EstLaCleDeDev(key))
                return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                    "The provisioned manifest public key is the development key; it is never trusted.");

            return Result<byte[]>.Ok(key);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                $"Manifest public key read failed: {ex.Message}");
        }
    }

    /// <summary>Un <see cref="IManifestVerifier"/> en vérification seule, ou Fail sans clé valide et fiable au repos.
    /// Jamais de signataire.</summary>
    public static Result<IManifestVerifier> LoadVerifier(string path, bool enforceTrust = true)
    {
        var key = Load(path, enforceTrust);
        if (!key.IsOk)
            return Result<IManifestVerifier>.Fail(key.Error!.Value.Code, key.Error.Value.Message);

        try
        {
            return Result<IManifestVerifier>.Ok(new Ed25519ManifestVerifier(key.Value!));
        }
        catch (Exception ex)
        {
            return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                $"Manifest public key could not be imported: {ex.Message}");
        }
    }
}
