namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// Docteur BIOS (socle). Contrats du cœur détection→comparaison→Findings.
//
// Stratégie arrêtée : V1 = docteur BIOS = lire + guider +
// prouver, jamais d'écriture ni de script BIOS. Le conseiller ici ne fait que lire et
// comparer : il prend le HardwareInventory + SettingsState déjà produits par le scan (il ne
// les redétecte jamais) et émet les Findings firmware qui concernent cette machine.
//
// Honnêteté : un réglage illisible = un Finding honnête
// « non lu + pourquoi », jamais un état inventé. « Rien à régler dans le
// BIOS » est un résultat valide, assumé.
//
// Étapes de guide / QR / page mobile / boucle de vérification / IBiosAutomationProvider
// viennent plus tard, volontairement hors de ces contrats de socle.
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
/// La place d'un réglage firmware dans la matrice d'action.
/// V1 émet <see cref="Guide"/> pour tout ce qui doit changer (lire + guider +
/// prouver). <see cref="Read"/> sert aux états d'information, déjà bons ou illisibles.
/// <see cref="AutoOfficial"/> / <see cref="LabWhitelist"/> / <see cref="Forbidden"/>
/// décrivent la voie d'écriture qu'une future version pourrait (ou ne doit jamais) prendre :
/// portés par le Finding pour l'honnêteté et l'UX, mais le socle n'écrit jamais.
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
/// Le sens conseillé pour un réglage firmware. Les constats du socle portent une clé
/// de libellé honnête (<see cref="BiosFinding.RecommendedActionKey"/>), pas d'étapes de
/// navigation dans le BIOS (elles viennent avec le guide).
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
/// Une estimation honnête du gain d'un réglage firmware mal posé : une plage sourcée avec
/// une réserve, jamais un % promis tout seul. Même discipline que la <c>GainEvidence</c> des
/// profils (« on prouve, on ne promet pas ») : une plage pire cas/typique liée à une
/// source citée, plus une réserve qui dit quand c'est ≈0 (déjà bien réglé) ou conditionnel
/// (par exemple seulement si mal routé). Aucun chiffre inventé : un réglage sans chiffre
/// sourcé porte une plage null et s'appuie sur la réserve (par exemple selon le jeu).
/// </summary>
/// <param name="MinPct">Bas de la plage honnête (par exemple ≈0 si déjà bien réglé) ; null = non chiffré.</param>
/// <param name="MaxPct">Haut de la plage honnête (gain au mieux) ; null = non chiffré.</param>
/// <param name="Conditional">
/// Vrai quand le gain n'existe que sous condition (par exemple le réglage est vraiment
/// mal posé ou mal routé) et vaut sans doute ≈0 sinon : pilote une formulation conditionnelle dans l'UI.
/// </param>
/// <param name="SourceKey">Clé resx qui cite la source du dépôt (le document de décision). Jamais vide.</param>
/// <param name="CaveatKey">Clé resx de la réserve d'honnêteté (« ≈0 si déjà correct » / selon le jeu…). Jamais vide.</param>
public record BiosGainEstimate(
    double? MinPct,
    double? MaxPct,
    bool Conditional,
    string SourceKey,
    string CaveatKey);

/// <summary>
/// Un Finding firmware qui concerne cette machine, produit par détection→comparaison
/// sur le <see cref="HardwareInventory"/> + <see cref="SettingsState"/> du scan.
/// Constat + raison + sens conseillé, rien d'autre : pas d'étapes de menu, pas d'écriture.
/// </summary>
/// <param name="Kind">Le réglage firmware concerné.</param>
/// <param name="Severity">Son poids quand il est mal posé (Info / Minor / Major).</param>
/// <param name="TitleKey">Clé resx du titre du constat (FR+EN). Jamais de texte en dur.</param>
/// <param name="RationaleKey">Clé resx du « pourquoi » (honnêteté). Jamais de texte en dur.</param>
/// <param name="MatrixClass">Classe de la voie d'écriture du réglage.</param>
/// <param name="CurrentState">État lu s'il existe ; <see cref="TriState.Unknown"/> s'il est illisible.</param>
/// <param name="RecommendedAction">Le sens conseillé honnête (pas d'étapes de menu ici).</param>
/// <param name="RecommendedActionKey">Clé resx du libellé de l'action conseillée (FR+EN).</param>
/// <param name="Gain">
/// Une plage de gain honnête et sourcée, avec réserve, quand c'est mal posé (jamais un %
/// promis tout seul) ; null si non chiffré. Voir <see cref="BiosGainEstimate"/>.
/// </param>
/// <param name="StateNotReadable">
/// Vrai quand <see cref="CurrentState"/> n'a pas pu être lu (« non lu + pourquoi », honnête).
/// Le Finding dit alors l'inconnu et n'affirme jamais un état faux.
/// </param>
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
/// Détection→comparaison sur l'inventaire et les réglages déjà produits par le scan, qui émet
/// les Findings firmware de cette machine. Ne redétecte jamais, n'écrit jamais.
/// </summary>
public interface IBiosAdvisor
{
    /// <summary>
    /// Produit les Findings firmware qui s'appliquent à <paramref name="hw"/> selon
    /// <paramref name="state"/>. Une machine bien réglée donne une liste vide (résultat
    /// valide, assumé). Une entrée incohérente échoue proprement via <see cref="Result"/>.
    /// </summary>
    Result<IReadOnlyList<BiosFinding>> Advise(HardwareInventory hw, SettingsState state);
}
