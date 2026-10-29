using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// Le manifeste de mise à jour signé et les coutures de l'updater. Modèle de sécurité :
//   * le manifeste est la seule source de l'url, de l'empreinte (sha256) et de la version d'une charge ; signé par une
//     clé Ed25519 dédiée (UpdateTrust.ProductionPublicKey ; UpdateDevKeys, refusée en production, sert aux tests),
//     vérifiée avant d'agir sur le moindre champ. Aucune empreinte hors d'un manifeste signé ;
//   * ordre imposé : la signature d'abord ; seulement si Ok, la charge et son SHA-256 contre l'empreinte du manifeste ;
//   * jamais fail-open : toute incertitude garde la version installée.
// Chaque couture rend un Result pour les pannes attendues (réseau, délai, disque, signature) ; une exception imprévue
// s'arrête aux entrées publiques de l'updater (UPDATE_UNEXPECTED).

/// <summary>Le canal de mise à jour. Seule l'app existe : le nom voyage dans les octets signés, un manifeste signé pour
/// un autre canal ne peut donc pas passer pour une mise à jour de l'app.</summary>
public enum UpdateChannel
{
    /// <summary>Le binaire de l'app dans Program Files, remplacé par l'installeur signé que le joueur lance au clic.</summary>
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
    /// <summary>Étape 1 : la signature Ed25519 du manifeste sous la clé publique embarquée. Ok si et seulement si elle est
    /// valide sur les octets canoniques ; null / vide / altérée, champ altéré, clé étrangère ou panne crypto →
    /// Fail(UPDATE_SIG_INVALID). Ne lève jamais. Rien ne se télécharge avant Ok.</summary>
    Result VerifyManifest(UpdateManifest manifest);

    /// <summary>Étape 2 : le SHA-256 de <paramref name="payload"/> contre <paramref name="manifest"/>.Sha256 (manifeste
    /// déjà vérifié), et la non-rétrogradation contre <paramref name="installedVersion"/> (sautée seulement si
    /// <paramref name="allowDowngrade"/>, par exemple un canal bêta). Désaccord → Fail(UPDATE_HASH_MISMATCH), charge
    /// jamais exécutée ; version inférieure → Fail(UPDATE_DOWNGRADE). Ne lève jamais.</summary>
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

/// <summary>Couture de système de fichiers minimale, testable en mémoire (sans disque ni admin). Chaque membre rend un
/// Result : un échec d'E/S ne lève jamais.</summary>
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

    /// <summary>Suppression au mieux d'un dossier de préparation. Ne lève jamais ; un échec n'est pas fatal, Fail pour le
    /// journal.</summary>
    Result DeleteDirectory(string path);
}

