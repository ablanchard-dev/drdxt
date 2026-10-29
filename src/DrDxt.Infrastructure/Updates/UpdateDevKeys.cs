using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// ───────────────────────────────────────────────────────────────────────────────
//  EMBEDDED PUBLIC MATERIAL ONLY : aucun secret ne doit jamais vivre ici dans une version publiée.
// ───────────────────────────────────────────────────────────────────────────────
// La clé de dev des mises à jour, séparée de celle du manifeste (DrDxt.Reconciler.Manifest.ManifestDevKeys) : la
// compromission d'un canal n'atteint pas l'autre. La clé publique est sûre à livrer dans un binaire décompilable (elle
// ne fait que vérifier) ; la clé privée de signature des versions vit hors du client (build / CI / HSM, hors bande).
// La clé de production vit dans UpdateTrust.ProductionPublicKey ; celle-ci, de dev, y est refusée. Sa graine privée
// appariée ne vit que dans les assemblys de test (DrDxt.TestSupport.UpdateDevTestKeys, tests/Shared), comme celle du
// manifeste : aucun octet de clé privée dans une DLL livrée.
// ───────────────────────────────────────────────────────────────────────────────
public static class UpdateDevKeys
{
    /// <summary>La clé publique Ed25519 brute de 32 octets des manifestes de la chaîne de publication de test : elle ne
    /// fait que vérifier un <see cref="UpdateManifest"/>. Valeur de dev/test, graine dans les tests seulement, refusée en
    /// production par <see cref="UpdateTrust.Refuser"/>.</summary>
    public static byte[] UpdatePublicKey =>
    [
        0xEA, 0x3F, 0xA2, 0xFA, 0x3E, 0x66, 0x8B, 0x0B,
        0xA1, 0xA8, 0xCB, 0x3B, 0x30, 0x1B, 0x72, 0xAE,
        0x2C, 0xAB, 0x46, 0x15, 0xCC, 0x95, 0x41, 0xB2,
        0xE2, 0x03, 0x3B, 0x31, 0x95, 0xAC, 0xF6, 0x61,
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

    /// <summary>L'<see cref="IUpdateVerifier"/> en vérification seule sur la clé embarquée ; corrompue ou de mauvaise
    /// longueur : Fail(UPDATE_SIG_INVALID), sans lever. Reprend <c>ManifestDevKeys.CreateVerifier</c>.</summary>
    public static Result<IUpdateVerifier> CreateUpdateVerifier()
        => UpdateVerifier.Create(UpdatePublicKey);
}
