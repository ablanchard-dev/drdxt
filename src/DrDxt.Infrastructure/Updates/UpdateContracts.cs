using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// Le manifeste de mise à jour signé + les coutures sur lesquelles tourne l'updater.
//
// Modèle de sécurité :
//   * Un manifeste est la seule chose à laquelle le client se fie pour savoir où est une charge
//     (url), quelle empreinte elle doit avoir (sha256) et quelle version elle est. Il est signé
//     avec une clé Ed25519 dédiée aux mises à jour (UpdateDevKeys), vérifiée avant d'agir sur le
//     moindre de ses champs. On ne se fie jamais qu'à une empreinte venue d'un manifeste signé.
//   * L'ordre est imposé : vérifier d'abord la signature du manifeste ; seulement si Ok, récupérer
//     la charge et vérifier son SHA-256 contre l'empreinte du manifeste (désormais fiable).
//   * Jamais fail-open : toute incertitude garde la version installée actuelle.
//
// Chaque couture rend un Result pour qu'un réseau / disque / chemin crypto indisponible échoue
// proprement (jamais d'exception à travers la frontière).
// -----------------------------------------------------------------------

/// <summary>Le canal de mise à jour. Seule l'app existe : le nom voyage dans les octets signés, un manifeste signé pour
/// un autre canal ne peut donc pas passer pour une mise à jour de l'app.</summary>
public enum UpdateChannel
{
    /// <summary>Le binaire de l'app dans Program Files. Le remplacer exige l'élévation : l'échange est
    /// délégué à la couture typée <see cref="IElevatedFileReplacer"/>.</summary>
    App,
}

/// <summary>Le manifeste de mise à jour signé. La signature (Ed25519, base64) porte sur les octets
/// canoniques des quatre champs de confiance (channel, version, url, sha256). Le client la vérifie
/// avec la clé publique de mise à jour embarquée avant de toucher à l'url ou de se fier au sha256.</summary>
/// <param name="Channel">Le canal que décrit ce manifeste.</param>
/// <param name="Version">La version offerte (sémantique ; comparée à l'installée).</param>
/// <param name="Url">Où vit la charge. Jamais utilisée avant que la signature soit vérifiée.</param>
/// <param name="Sha256">Le SHA-256 attendu de la charge, en hexadécimal minuscule. Fiable seulement
/// après vérification de la signature (jamais une empreinte d'un manifeste non signé).</param>
/// <param name="Signature">Signature Ed25519 détachée (base64) sur les octets canoniques.</param>
public sealed record UpdateManifest(
    UpdateChannel Channel,
    string Version,
    string Url,
    string Sha256,
    string Signature);

/// <summary>La barrière de confiance des mises à jour. Vérifie d'abord la signature du manifeste,
/// puis (à part, seulement quand l'appelant a les octets) l'empreinte de la charge. À base de
/// Result et totale : toute altération / mauvaise clé / signature absente / empreinte en désaccord
/// / rétrogradation → Fail(UPDATE_*), jamais Ok. Jamais fail-open.</summary>
public interface IUpdateVerifier
{
    /// <summary>Étape 1 : vérifier la signature Ed25519 du manifeste sous la clé publique de mise à
    /// jour embarquée. Ok si et seulement si la signature est valide sur les octets canoniques du
    /// manifeste. Une signature null / vide / altérée, un champ altéré, une clé étrangère ou toute
    /// panne crypto → Fail(UPDATE_SIG_INVALID). Ne lève jamais, jamais Ok par défaut. L'appelant ne
    /// doit pas télécharger la charge tant que ceci ne rend pas Ok.</summary>
    Result VerifyManifest(UpdateManifest manifest);

    /// <summary>Étape 2 : vérifier la charge téléchargée contre le manifeste (déjà vérifié par
    /// signature). Calcule le SHA-256 de <paramref name="payload"/> et le compare à
    /// <paramref name="manifest"/>.Sha256. Impose aussi la règle de non-rétrogradation contre
    /// <paramref name="installedVersion"/> (sautée seulement si <paramref name="allowDowngrade"/> vaut
    /// true, par exemple un canal bêta). Empreinte en désaccord → Fail(UPDATE_HASH_MISMATCH) (charge
    /// jamais exécutée) ; version inférieure → Fail(UPDATE_DOWNGRADE). Ne lève jamais.</summary>
    Result VerifyPayload(
        UpdateManifest manifest, byte[] payload, string installedVersion, bool allowDowngrade);
}

/// <summary>La couture du flux de mise à jour : récupère le manifeste signé et les octets de la
/// charge d'un canal par le transport injecté (non epingle : le serveur de mise a jour n'a
/// pas encore d'empreintes). Un faux le remplace en test. Pas de réseau → Fail(UPDATE_OFFLINE) ;
/// l'app continue sur la version actuelle (les mises à jour ne bloquent jamais).</summary>
public interface IUpdateFeed
{
    /// <summary>Récupère le dernier manifeste signé de <paramref name="channel"/>.
    /// Hors ligne / panne de transport → Fail(UPDATE_OFFLINE). Ok(null) quand le flux est joignable
    /// mais n'offre aucun manifeste (déjà à jour).</summary>
    Task<Result<UpdateManifest?>> GetManifestAsync(UpdateChannel channel, CancellationToken ct);

    /// <summary>Télécharge la charge à <paramref name="url"/> (l'url ne vient que d'un manifeste
    /// déjà vérifié par signature). Hors ligne / panne de transport → Fail(UPDATE_OFFLINE).</summary>
    Task<Result<byte[]>> DownloadAsync(string url, CancellationToken ct);
}

/// <summary>Couture de système de fichiers minimale pour que la préparation soit entièrement
/// testable avec un faux en mémoire (sans vrai disque ni admin). Chaque membre est emballé dans un
/// Result : un échec d'E/S ne lève jamais à travers la frontière.</summary>
public interface IUpdateFileSystem
{
    /// <summary>Vrai si et seulement si le dossier existe.</summary>
    bool DirectoryExists(string path);

    /// <summary>Les octets d'un fichier, ou <c>null</c> s'il est absent ou illisible. Sert a ne pas
    /// retelecharger une charge deja stagee (elle est de toute facon reverifiee). Par defaut : null.</summary>
    byte[]? TryReadFile(string path) => null;

    /// <summary>Crée le dossier (et ses parents). Échec d'E/S → Fail.</summary>
    Result CreateDirectory(string path);

    /// <summary>Écrit des octets dans un fichier, en l'écrasant. Échec d'E/S → Fail.</summary>
    Result WriteFile(string path, byte[] bytes);

    /// <summary>Suppression au mieux d'un dossier de préparation (nettoyage). Ne lève jamais ; ne pas
    /// réussir à nettoyer un reste n'est pas fatal et rend Fail pour que l'appelant le journalise.</summary>
    Result DeleteDirectory(string path);
}

/// <summary>La couture élevée, typée et admise, qui remplace des binaires signés dans Program
/// Files. Ce n'est pas une primitive d'écriture quelconque : sa seule opération est « remplacer ces
/// fichiers déjà vérifiés et préparés ». Pour l'instant, l'élévation réelle et le branchement au
/// helper sont reportés (il faut l'UAC) : l'implémentation par défaut échoue proprement
/// (UPDATE_REPLACER_UNAVAILABLE), exactement comme les autres coutures reportées. Un échec de
/// vérification à n'importe quelle étape antérieure veut dire qu'elle n'est même pas appelée.</summary>
public interface IElevatedFileReplacer
{
    /// <summary>Remplace les fichiers de l'app en service à partir d'une charge vérifiée et préparée.
    /// Appelée seulement après que la signature du manifeste, l'empreinte de la charge et la règle de
    /// non-rétrogradation sont toutes passées et que la charge a été préparée atomiquement.
    /// Implémentation reportée → Fail(UPDATE_REPLACER_UNAVAILABLE) (rien de remplacé) ; l'implémentation
    /// réelle fait l'échange typé.</summary>
    Task<Result> ReplaceAppFilesAsync(string stagedPayloadPath, string targetInstallDir, CancellationToken ct);
}
