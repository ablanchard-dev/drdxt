namespace DrDxt.TestSupport;

// -----------------------------------------------------------------------
// Gabarit réservé aux tests : la graine privée de dev/test appariée à
// DrDxt.Infrastructure.Updates.UpdateDevKeys.UpdatePublicKey.
//
// Même règle que ManifestDevTestKeys : une graine de signature ne part dans aucun binaire livré. Quiconque la lirait dans
// DrDxt.Infrastructure.dll signerait un manifeste que la clé publique de dev accepte ; UpdateTrust refuse cette clé en
// production, mais un refus ne remplace pas l'absence. Liée dans les projets de test qui signent des mises à jour ou des
// données d'essai (une seule source).
// -----------------------------------------------------------------------
internal static class UpdateDevTestKeys
{
    /// <summary>La graine privée Ed25519 brute de 32 octets appariée à <c>UpdateDevKeys.UpdatePublicKey</c>. Réservée aux
    /// tests : sert à signer des manifestes et des données d'essai que la clé publique de dev vérifie.</summary>
    public static byte[] DevPrivateSeed =>
    [
        0x58, 0xB9, 0x4A, 0xA6, 0xCB, 0xC3, 0x06, 0x66,
        0x9B, 0x83, 0xB3, 0xD7, 0xB2, 0x51, 0x6F, 0x86,
        0x16, 0x05, 0x3C, 0x05, 0x1C, 0x2F, 0x47, 0xDF,
        0x70, 0x53, 0x8B, 0x00, 0xE8, 0x1E, 0x92, 0xF7,
    ];
}
