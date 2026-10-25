using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// Le manifeste DesiredState scellé + les coutures qui le protègent.
//
// Modèle de sécurité de référence :
//   * Intégrité = une signature asymétrique sur les octets canoniques. Le commit se fait avec la
//     clé privée ; le service réconciliateur ne tient que la clé publique et vérifie avant de
//     réappliquer quoi que ce soit.
//   * DPAPI / MAC symétrique sont explicitement refusés pour l'intégrité du manifeste : le
//     propriétaire de la machine peut redériver une clé symétrique et forger un manifeste « valide ».
//   * Défense en profondeur = le fichier vit sous une ACL ProgramData en écriture admin seulement
//     (IManifestStorage). L'ACL complète la signature ; elle ne la remplace jamais.
//
// Chaque couture rend un Result pour qu'un système de fichiers ou un chemin cryptographique
// indisponible échoue proprement (jamais d'exception à travers la frontière).
// -----------------------------------------------------------------------

/// <summary>L'enveloppe versionnée sur disque qui emballe un <see cref="DesiredState"/>
/// commité. Le corps ne contient que des leviers validés et commités (un levier n'y entre que si
/// son test de stabilité a passé et que son bénéfice mesuré a dépassé la marge). C'est
/// l'enveloppe qui est signée.
///
/// La cle de la machine protege contre un programme non eleve, jamais contre le proprietaire
/// administrateur de la machine, qui peut la lire sous %ProgramData%\DrDxt et resigner ce qu'il veut.
/// Ce manifeste ne dit donc que ce que DrDXT a pose, ce qui est exactement ce que cette cle sait garantir.
///
/// Un manifeste plus ancien qui porte encore un <c>expiresAt</c> le voit simplement ignore
/// a la relecture (champ inconnu), sa signature reste valide et ses origines restent rendues a la
/// desinstallation.</summary>
public sealed record DesiredStateManifest(
    int SchemaVersion,
    DesiredState State,
    DateTimeOffset CommittedAt)
{
    /// <summary>La seule version de schéma que ce build lit et écrit. Un manifeste d'une autre
    /// version est refusé proprement (aucune supposition).</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Lit tout le manifeste signé et vérifié, pour savoir s'il y a quelque chose
/// d'armé. Implémenté par
/// <c>SignedDesiredStateStore</c> à côté de <see cref="IDesiredStateStore"/>, avec exactement le
/// même chemin « vérifier avant d'analyser » (la signature Ed25519 est l'autorité d'intégrité).
/// Rend Ok(null) quand aucun manifeste n'existe encore (première installation) ; une altération,
/// une erreur de schéma ou d'E/S échoue proprement (fail-closed).</summary>
public interface ISignedManifestReader
{
    Task<Result<DesiredStateManifest?>> LoadManifestAsync();
}

/// <summary>La signature détachée + les octets canoniques sur lesquels elle a été calculée.
/// Enregistrée avec le corps du manifeste pour que le vérificateur contrôle les octets exacts.</summary>
public sealed record SignedManifestEnvelope(
    string CanonicalJson,
    string SignatureBase64);

// -----------------------------------------------------------------------
// ISignatureScheme : la couture asymétrique de signature / vérification.
//
// Le réconciliateur (côté vérification) se construit avec un vérificateur qui ne tient que la
// clé publique. La capacité de signer est un objet distinct qui tient la clé privée ; elle n'est
// jamais injectée dans le chemin de réconciliation. Les tests utilisent un faux en mémoire ; la
// production utilise Ed25519SignatureScheme (NSec / libsodium).
// -----------------------------------------------------------------------

/// <summary>Vérifie une signature détachée sur une charge d'octets canoniques avec la seule
/// clé publique. Tenu par le côté réconciliateur / vérificateur.</summary>
public interface IManifestVerifier
{
    /// <summary>Vrai si et seulement si <paramref name="signature"/> est une signature valide de
    /// <paramref name="canonicalBytes"/> sous la clé publique. Ne lève jamais : une signature
    /// malformée ou de mauvaise longueur rend false (fail-closed).</summary>
    bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature);
}

/// <summary>Signe une charge d'octets canoniques avec la clé privée. Tenu seulement par le helper
/// élevé (MachineManifestCommit), jamais par l'App, jamais injecté dans le chemin de réconciliation
/// / de vérification. La clé est celle de la machine (MachineManifestKey, réservée aux admins sous
/// %ProgramData%\DrDxt) ; le service réconciliateur et l'App ne reçoivent que la clé publique.</summary>
public interface IManifestSigner
{
    /// <summary>Produit une signature détachée de <paramref name="canonicalBytes"/>.
    /// Échec propre sur toute erreur cryptographique.</summary>
    Result<byte[]> Sign(ReadOnlySpan<byte> canonicalBytes);
}

// -----------------------------------------------------------------------
// IManifestStorage : la couture chemin + ACL (défense en profondeur).
//
// La production écrit sous %ProgramData%\DrDxt avec une DACL en écriture admin seulement. Les
// tests unitaires mettent un stockage en mémoire à la place : ils ne touchent jamais le vrai
// système de fichiers et ne demandent pas l'admin. Les écritures sont atomiques (fichier
// temporaire + remplacement) : un plantage ne laisse jamais un manifeste à moitié écrit.
// -----------------------------------------------------------------------
public interface IManifestStorage
{
    /// <summary>Enregistre atomiquement l'enveloppe signée sous la racine en écriture admin.
    /// Échec d'E/S → Fail(ErrorCodes.IoFailure).</summary>
    Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct);

    /// <summary>Lit l'enveloppe enregistrée. Rend Ok(null) quand aucun manifeste n'existe encore
    /// (première installation). Échec d'E/S → Fail(ErrorCodes.IoFailure).</summary>
    Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct);
}
