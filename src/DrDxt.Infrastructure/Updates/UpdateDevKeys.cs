using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// ───────────────────────────────────────────────────────────────────────────────
//  EMBEDDED PUBLIC MATERIAL ONLY : aucun secret ne doit jamais vivre ici dans une version publiée.
// ───────────────────────────────────────────────────────────────────────────────
//
// C'est la racine de confiance du client pour la couche de mise à jour automatique. C'est une
// paire de clés de signature dédiée aux mises à jour, séparée exprès des clés de licence
// (DrDxt.Licensing.EmbeddedKeys.LicensePublicKey) et des clés du manifeste / réconciliateur
// (DrDxt.Reconciler.Manifest.ManifestDevKeys.PublicKey). Décision : cloisonner le canal de mise à
// jour pour que la compromission d'une clé de signature n'atteigne jamais les autres.
//
// La clé publique d'ici est sûre à livrer dans un binaire décompilable : elle ne peut que vérifier
// un manifeste de mise à jour, jamais en frapper un. La clé privée de signature des versions vit
// hors du client (build / CI / HSM, hors bande), exactement comme pour ManifestDevKeys.
//
// Production (durcissement de version, à faire obligatoirement au moment de publier, comme
// ManifestDevKeys / EmbeddedKeys) : remplacer UpdatePublicKey par la vraie clé de vérification des
// versions et ne jamais livrer DevPrivateSeed. La graine de dev ci-dessous est l'unique exception
// clairement balisée (avec sa propre bannière) et n'existe que pour que les tests unitaires signent
// un manifeste d'essai contre la clé publique embarquée sans étape de fourniture : aucun chemin de
// code de production ne la lit jamais.
// ───────────────────────────────────────────────────────────────────────────────
public static class UpdateDevKeys
{
    /// <summary>La clé publique Ed25519 brute de 32 octets associée aux manifestes que signe la chaîne
    /// de publication. Sûre à livrer : elle ne peut que vérifier un <see cref="UpdateManifest"/>,
    /// jamais en signer un. Une version publiée la remplace par la vraie clé de vérification (hors
    /// bande). Valeur DEV/TEST associée à <see cref="DevPrivateSeed"/>. Dédiée aux mises à jour : ce
    /// n'est pas la clé de licence ni celle du manifeste.</summary>
    public static byte[] UpdatePublicKey =>
    [
        0xEA, 0x3F, 0xA2, 0xFA, 0x3E, 0x66, 0x8B, 0x0B,
        0xA1, 0xA8, 0xCB, 0x3B, 0x30, 0x1B, 0x72, 0xAE,
        0x2C, 0xAB, 0x46, 0x15, 0xCC, 0x95, 0x41, 0xB2,
        0xE2, 0x03, 0x3B, 0x31, 0x95, 0xAC, 0xF6, 0x61,
    ];

    // ───────────────────────────────────────────────────────────────────────────
    //   DEV / TEST PRIVATE SEED : pas pour la production, NEVER READ AT RUNTIME (jamais lue à l'exécution)
    // ───────────────────────────────────────────────────────────────────────────
    //  La graine privée Ed25519 brute de 32 octets associée à UpdatePublicKey ci-dessus. Commitée
    //  seulement pour que les tests unitaires signent un UpdateManifest d'essai que la clé publique
    //  embarquée vérifie ensuite, ce qui garde la suite de tests hermétique. Aucun chemin de code de
    //  production ne référence cette graine. En production, les manifestes sont signés par la chaîne
    //  de publication, qui tient la vraie clé privée hors bande ; une version publiée ne livre que
    //  UpdatePublicKey. Quiconque détient cette graine de dev peut forger un manifeste que seule la clé
    //  publique de dev accepterait : c'est exactement pourquoi la clé publique est changée à la publication.
    //  La paire a tourné le 16/10/2026 : l'ancienne graine avait été recopiée hors du dépôt. Elle n'a jamais rien
    //  ouvert en production (UpdateTrust refuse la clé de dev), le changement est une hygiène.
    // ───────────────────────────────────────────────────────────────────────────

    /// <summary>DEV/TEST seulement : la graine associée à <see cref="UpdatePublicKey"/>, pour signer
    /// des données d'essai en test. Jamais utilisée par le code de production.</summary>
    public static byte[] DevPrivateSeed =>
    [
        0x58, 0xB9, 0x4A, 0xA6, 0xCB, 0xC3, 0x06, 0x66,
        0x9B, 0x83, 0xB3, 0xD7, 0xB2, 0x51, 0x6F, 0x86,
        0x16, 0x05, 0x3C, 0x05, 0x1C, 0x2F, 0x47, 0xDF,
        0x70, 0x53, 0x8B, 0x00, 0xE8, 0x1E, 0x92, 0xF7,
    ];

    /// <summary>Les clés publiques de dev retirées : leur graine est connue hors du dépôt, aucune ne doit jamais vérifier
    /// une mise à jour de production (<see cref="UpdateTrust.CreateVerifier"/> les refuse comme la clé de dev courante).</summary>
    public static IReadOnlyList<byte[]> ClesDevRetirees =>
    [
        [
            0xF0, 0x73, 0x64, 0x29, 0x03, 0x42, 0x90, 0x58,
            0x93, 0xC8, 0x7B, 0xDD, 0xEB, 0x81, 0x89, 0xED,
            0x36, 0x49, 0x64, 0x14, 0xEE, 0x6F, 0x5D, 0x8D,
            0x59, 0x0C, 0xE7, 0xCB, 0x35, 0xEC, 0x23, 0xD8,
        ],
    ];

    /// <summary>Construit l'<see cref="IUpdateVerifier"/> en vérification seule à partir de la clé
    /// publique embarquée. Échec propre : une clé embarquée corrompue ou de mauvaise longueur rend
    /// Fail(UPDATE_SIG_INVALID) au lieu de lever (protège d'un futur mauvais échange de clé). Reprend
    /// <c>ManifestDevKeys.CreateVerifier</c> / <c>EmbeddedKeys.CreateLicenseVerifier</c>.</summary>
    public static Result<IUpdateVerifier> CreateUpdateVerifier()
        => UpdateVerifier.Create(UpdatePublicKey);
}
