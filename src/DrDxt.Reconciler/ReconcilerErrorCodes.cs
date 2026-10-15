namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Codes d'erreur du réconciliateur : locaux au module, dans un espace de noms pour que les
// littéraux ne soient jamais éparpillés. Les ErrorCodes de Core (IoFailure,
// ManifestTampered, OpNotAllowed…) sont repris là où ils conviennent déjà.
// -----------------------------------------------------------------------
public static class ReconcilerErrorCodes
{
    // Un manifeste à schemaVersion inconnu / futur → refus, jamais de supposition.
    public const string ManifestSchema = "MANIFEST_SCHEMA";

    // Un manifeste commité porte un levier dont la catégorie n'est pas réconciliable côté OS
    // (persistée dans le firmware : XMP/EXPO/undervolt BIOS) → refus à l'enregistrement. Le
    // réconciliateur ne possède que des réglages volatils côté OS.
    public const string ManifestNonReconcilable = "MANIFEST_NON_RECONCILABLE";

    // Un tweakId arrivé dans la boucle de réconciliation sans correspondance avec une op typée admise
    // (hors de l'ensemble connu du manifeste vérifié) → refus, journal de sécurité.
    public const string TweakNotAllowlisted = "TWEAK_NOT_ALLOWLISTED";

    // Un tweakId vise une surface protégée (Defender / Windows Update / BITS) →
    // refus strict même s'il est dans le manifeste (liste noire).
    public const string ProtectedSurface = "PROTECTED_SURFACE";

    // Un ou plusieurs éléments n'ont pas pu être réconciliés ; les autres ont abouti. Regroupé,
    // jamais un état à moitié appliqué.
    public const string ReconcilePartial = "RECONCILE_PARTIAL";

    // Une écriture privilégiée a rendu Ok mais la relecture de confirmation ne montre pas la valeur
    // voulue (par exemple une GPO la réimpose en silence) → élément marqué en échec.
    public const string ConfirmFailed = "RECONCILE_CONFIRM_FAILED";

    // Une sonde de lecture a échoué pendant la classification de dérive → échec propre, aucune
    // supposition, aucune écriture.
    public const string ProbeFailed = "RECONCILE_PROBE_FAILED";

    // Désinstallation propre : la boucle a été arrêtée, le bail désarmé et l'hôte retiré, mais une
    // ou plusieurs écritures « retour à l'origine » ont échoué (ou n'ont pas été confirmées).
    // Regroupé et rapporté, jamais une installation orpheline qui réconcilie encore.
    public const string UninstallRevertPartial = "UNINSTALL_REVERT_PARTIAL";
}
