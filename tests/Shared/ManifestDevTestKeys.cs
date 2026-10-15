namespace DrDxt.TestSupport;

// -----------------------------------------------------------------------
// Gabarit réservé aux tests : la graine privée de dev/test appariée à
// DrDxt.Reconciler.Manifest.ManifestDevKeys.PublicKey.
//
// Pourquoi ceci vit dans tests/ et pas dans src/ :
// une clé privée de signature de manifeste utilisable ne doit jamais partir dans un binaire de
// production ; quiconque décompilerait DrDxt.Reconciler.dll et obtiendrait cette graine pourrait
// forger un manifeste d'état voulu que le réconciliateur du démarrage réapplique. La clé publique
// de 32 octets reste dans src/ (ManifestDevKeys.PublicKey : sûre, elle ne peut que vérifier) ; sa
// graine privée appariée vit ici, compilée seulement dans les assemblys de test (dans toute
// configuration, Release comprise), pour que les tests hermétiques signent un manifeste de gabarit
// que la clé publique embarquée vérifie ensuite, avec zéro octet de clé privée dans toute DLL livrée.
//
// Ce fichier est lié dans les projets de test du réconciliateur, de l'hôte et de l'App (une seule
// source : aucune copie de la graine par projet qui pourrait dériver).
// -----------------------------------------------------------------------
internal static class ManifestDevTestKeys
{
    /// <summary>La graine privée Ed25519 brute de 32 octets appariée à
    /// <c>ManifestDevKeys.PublicKey</c>. Réservée aux tests : sert à signer les manifestes de gabarit
    /// que la clé publique embarquée vérifie. Jamais présente dans un binaire livré.</summary>
    public static byte[] DevPrivateSeed =>
    [
        0x31, 0x5D, 0xED, 0x95, 0x60, 0xB6, 0xA4, 0x94,
        0x1D, 0x5D, 0x11, 0xD4, 0x7A, 0x97, 0xE3, 0x14,
        0x73, 0xA0, 0x94, 0x17, 0xAE, 0x1E, 0x4C, 0xA0,
        0xD8, 0x9D, 0x85, 0x3E, 0x67, 0xB2, 0x6E, 0x1A,
    ];
}
