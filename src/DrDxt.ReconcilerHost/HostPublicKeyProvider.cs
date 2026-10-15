using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// HostPublicKeyProvider : charge la clé publique du manifeste, en vérification seule.
//
// L'hôte réconciliateur vérifie les manifestes et ne les signe jamais : il ne reçoit que la clé
// publique Ed25519 brute de 32 octets. La clé privée vit bien sur cette machine, mais seul le
// helper élevé la lit, jamais ce service (voir SignedDesiredStateStore, garde de la clé). Ceci lit
// la clé publique dans un fichier sous la racine du manifeste en écriture admin.
//
// Fail-closed : une clé absente ou malformée rend Fail. L'hôte n'a alors aucun vérificateur : il
// ne peut vérifier aucun manifeste et (à raison) ne réconcilie pas ; il ne se rabat jamais sur une
// lecture non vérifiée. Une clé privée absente / vide est la norme et n'est jamais branchée ici
// (ce fournisseur ne produit jamais qu'un vérificateur).
//
// La clé est celle de la machine : le helper élevé la dérive de la clé de signature de la machine
// (MachineManifestKey) et l'écrit à côté du manifeste au premier CommitManifestOp. Ce chargeur
// est le côté consommation ; il n'embarque aucune clé.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class HostPublicKeyProvider
{
    private const string FileName = "manifest-public.key";
    private const int Ed25519PublicKeyLength = 32;

    /// <summary>Le chemin par défaut de la clé publique sous la racine du manifeste en écriture admin
    /// (voisin du manifeste + du sidecar).</summary>
    public static string DefaultPath()
        => Path.Combine(ProgramDataManifestStorage.DefaultRoot(), FileName);

    /// <summary>Charge la clé publique Ed25519 brute de 32 octets. Absente / mauvaise longueur / non
    /// fiable au repos côté admin → Fail (fail-closed : pas de clé ⇒ pas de vérificateur ⇒ l'hôte
    /// refuse de réconcilier).</summary>
    /// <param name="enforceTrust">Si true (production), le fichier n'est tenu pour fiable que si son
    /// propriétaire est Administrateurs/SYSTEM et que la racine n'est pas inscriptible par un non-admin
    /// (<see cref="TrustedManifestRoot.EnsureTrustedFile"/>) : une clé falsifiée déposée dans une
    /// racine possédée par l'utilisateur est rejetée (BLOCKER #1a). Les tests passent false pour faire
    /// l'aller-retour d'un fichier temporaire possédé par l'utilisateur de test (non admin).</param>
    public static Result<byte[]> Load(string path, bool enforceTrust = true)
    {
        try
        {
            if (!File.Exists(path))
                return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                    $"Manifest public key not provisioned at '{path}'.");

            // Intégrité au repos : un fichier de clé présent mais non fiable (possédé par l'utilisateur / dans
            // une racine inscriptible par l'utilisateur) est une attaque, pas une clé absente : refus ferme.
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

    /// <summary>Construit un <see cref="IManifestVerifier"/> en vérification seule à partir de la clé
    /// publique fournie, ou Fail si aucune clé valide (et fiable au repos côté admin) n'est présente.
    /// Ne produit jamais de signataire.</summary>
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
