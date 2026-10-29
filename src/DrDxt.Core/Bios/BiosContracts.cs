namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// Docteur BIOS (socle) : détection→comparaison→Findings. V1 = lire + guider + prouver, jamais d'écriture ni de script
// BIOS. Le conseiller lit le HardwareInventory + SettingsState du scan (sans redétecter) et émet les Findings firmware
// de cette machine. Un réglage illisible = « non lu + pourquoi », jamais un état inventé ; « rien à régler » est un
// résultat valide. Guide, QR, page mobile et vérification après redémarrage vivent hors de ces contrats.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Le réglage firmware que concerne un <see cref="BiosFinding"/>.
/// </summary>
public enum FirmwareSettingKind
{
    /// <summary>Profil d'overclocking mémoire (XMP chez Intel / EXPO chez AMD / anciens DOCP/A-XMP).</summary>
    XmpExpo,

    /// <summary>Resizable BAR (NVIDIA) / Smart Access Memory (AMD).</summary>
    ReBar,

    /// <summary>Above 4G Decoding, un prérequis de ReBAR.</summary>
    Above4G,

    /// <summary>Compatibility Support Module : doit être coupé (UEFI) pour ReBAR ; dépend du mode de démarrage.</summary>
    Csm,

    /// <summary>Routage CPPC / Preferred Cores des X3D AMD multi-CCD (la route V-Cache).</summary>
    X3dCppc,

    /// <summary>Génération du lien PCIe (Auto / Gen3 / Gen4 / Gen5) sur le port du GPU.</summary>
    PcieGen,

    /// <summary>Version du firmware BIOS / AGESA (mise à jour conseillée si ancienne).</summary>
    BiosVersion,
}

/// <summary>
/// La place d'un réglage firmware dans la matrice d'action. V1 émet <see cref="Guide"/> pour tout ce qui doit changer,
/// <see cref="Read"/> pour l'information. Les autres classes décrivent la voie d'écriture qu'une future version pourrait
/// (ou ne doit jamais) prendre : portées par le Finding pour l'honnêteté, le socle n'écrit jamais.
/// </summary>
public enum BiosMatrixClass
{
    /// <summary>Lecture seule : état rapporté (bon, ou illisible). Aucun changement proposé.</summary>
    Read,

    /// <summary>Guider l'utilisateur dans le changement BIOS exact (défaut V1 pour les corrections).</summary>
    Guide,

    /// <summary>Automatique via une API/CLI officielle et documentée du fabricant, sur machine prise en charge (V2 seulement).</summary>
    AutoOfficial,

    /// <summary>Écriture sur liste blanche du labo : carte + BIOS + réglage exacts + sauvegarde (V2/V3 seulement).</summary>
    LabWhitelist,

    /// <summary>Une voie d'écriture interdite (écriture UEFI brute, WinRing0, flash BIOS/VBIOS, …).</summary>
    Forbidden,
}

/// <summary>
/// Le sens conseillé pour un réglage firmware : une clé de libellé (<see cref="BiosFinding.RecommendedActionKey"/>),
/// pas d'étapes de menu (elles viennent avec le guide).
/// </summary>
public enum BiosRecommendedAction
{
    /// <summary>Rien à faire : information seule (déjà bon, ou simplement rapporté).</summary>
    None,

    /// <summary>Activer le réglage (par exemple XMP/EXPO, ReBAR, Above-4G).</summary>
    Enable,

    /// <summary>Couper le réglage (par exemple le CSM en démarrage UEFI, pour ReBAR).</summary>
    Disable,

    /// <summary>Mettre le réglage sur sa route prise en charge (par exemple CPPC = Driver / Auto pour X3D).</summary>
    SetSupportedRoute,

    /// <summary>Mettre à jour le firmware (BIOS / AGESA) vers une version plus récente conseillée.</summary>
    Update,

    /// <summary>État illisible : vérifier à la main dans le BIOS (inconnu honnête).</summary>
    Verify,
}

/// <summary>
/// Le gain d'un réglage firmware mal posé : une plage sourcée avec une réserve, jamais un % promis seul (même discipline
/// que la <c>GainEvidence</c> des profils). La réserve dit quand c'est ≈0 ou conditionnel ; sans chiffre sourcé, la
/// plage est null.
/// </summary>
/// <param name="MinPct">Bas de la plage honnête (par exemple ≈0 si déjà bien réglé) ; null = non chiffré.</param>
/// <param name="MaxPct">Haut de la plage honnête (gain au mieux) ; null = non chiffré.</param>
/// <param name="Conditional">Vrai quand le gain n'existe que sous condition (réglage vraiment mal posé ou mal routé), ≈0
/// sinon : pilote une formulation conditionnelle dans l'UI.</param>
/// <param name="SourceKey">Clé resx qui cite la source du dépôt (le document de décision). Jamais vide.</param>
/// <param name="CaveatKey">Clé resx de la réserve d'honnêteté (« ≈0 si déjà correct » / selon le jeu…). Jamais vide.</param>
public record BiosGainEstimate(
    double? MinPct,
    double? MaxPct,
    bool Conditional,
    string SourceKey,
    string CaveatKey);

/// <summary>
/// Un Finding firmware de cette machine : constat + raison + sens conseillé, ni étapes de menu ni écriture.
/// </summary>
/// <param name="Kind">Le réglage firmware concerné.</param>
/// <param name="Severity">Son poids quand il est mal posé (Info / Minor / Major).</param>
/// <param name="TitleKey">Clé resx du titre du constat (FR+EN). Jamais de texte en dur.</param>
/// <param name="RationaleKey">Clé resx du « pourquoi » (honnêteté). Jamais de texte en dur.</param>
/// <param name="MatrixClass">Classe de la voie d'écriture du réglage.</param>
/// <param name="CurrentState">État lu s'il existe ; <see cref="TriState.Unknown"/> s'il est illisible.</param>
/// <param name="RecommendedAction">Le sens conseillé honnête (pas d'étapes de menu ici).</param>
/// <param name="RecommendedActionKey">Clé resx du libellé de l'action conseillée (FR+EN).</param>
/// <param name="Gain">Plage sourcée avec réserve quand c'est mal posé ; null si non chiffré. Voir
/// <see cref="BiosGainEstimate"/>.</param>
/// <param name="StateNotReadable">Vrai quand <see cref="CurrentState"/> n'a pas pu être lu : le Finding dit l'inconnu,
/// jamais un état faux.</param>
public record BiosFinding(
    FirmwareSettingKind Kind,
    Severity Severity,
    string TitleKey,
    string RationaleKey,
    BiosMatrixClass MatrixClass,
    TriState CurrentState,
    BiosRecommendedAction RecommendedAction,
    string RecommendedActionKey,
    BiosGainEstimate? Gain,
    bool StateNotReadable);

/// <summary>
/// Émet les Findings firmware de cette machine depuis l'inventaire et les réglages du scan. Ne redétecte jamais,
/// n'écrit jamais.
/// </summary>
public interface IBiosAdvisor
{
    /// <summary>
    /// Les Findings firmware de <paramref name="hw"/> selon <paramref name="state"/> ; machine bien réglée = liste vide.
    /// Une entrée incohérente échoue via <see cref="Result"/>.
    /// </summary>
    Result<IReadOnlyList<BiosFinding>> Advise(HardwareInventory hw, SettingsState state);
}
