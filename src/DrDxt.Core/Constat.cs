namespace DrDxt.Core;

/// <summary>Ce qu'on sait d'un constat : lu, pas lu (angle mort dit), ou sans objet sur cette machine.</summary>
public enum EtatConstat { Lu, NonLu, SansObjet }

/// <summary>Ce que le joueur peut défaire. <c>SansObjet</c> = DrDXT n'a rien écrit (un constat lu seulement).</summary>
public enum Annulation { SansObjet, Oui, AuRedemarrage, Non }

/// <summary>
/// 06/10/2026 (chantier B0) : la forme commune des constats que l'écran affiche, quelle que soit leur famille
/// (santé, scan, BIOS). L'UI n'a plus qu'à afficher ces champs ; elle ne calcule rien.
/// </summary>
/// <param name="Id">Stable d'un scan à l'autre : la clé du texte (scan, BIOS) ou « sante/domaine/nom ».</param>
/// <param name="Titre">Clé de ressource (scan, BIOS) ; libellé déjà en français (santé, dont les textes vivent dans Core).</param>
/// <param name="Valeur">Ce qui est lu, tel quel. null = non lu, ou une famille qui ne porte pas de valeur (scan).</param>
/// <param name="Pourquoi">Clé (BIOS) ou explication (santé). null = la famille n'en porte pas (scan, voir §9 F-1).</param>
/// <param name="Source">Ce qui a posé la valeur, ou la source du gain, quand elle est réellement connue.</param>
/// <param name="Geste">Ce que DrDXT propose : clé (BIOS) ou action (santé). null = rien à faire.</param>
public sealed record Constat(
    string Id,
    string Famille,
    string Categorie,
    Severity Gravite,
    EtatConstat Etat,
    string Titre,
    string? Valeur,
    string? Attendu,
    string? Pourquoi,
    string? Source,
    string? Geste,
    Annulation Annulation)
{
    // Seul KnownHarmful est grave : « différent » n'est pas « endommagé », et un état non lu est un trou dans notre
    // connaissance, pas une accusation.
    public static Severity GraviteDe(RepairStatus s) => s switch
    {
        RepairStatus.KnownHarmful => Severity.Major,
        RepairStatus.Suspicious => Severity.Minor,
        _ => Severity.Info,
    };

    public static Constat Depuis(RepairFinding f) => new(
        $"sante/{f.Domaine}/{f.Nom}", "sante", f.Domaine, GraviteDe(f.Statut),
        f.Statut == RepairStatus.Unknown || f.ValeurCourante is null ? EtatConstat.NonLu : EtatConstat.Lu,
        RepairLibelles.Nom(f.Nom), f.ValeurCourante, f.ValeurAttendue, f.Explication, f.Provenance,
        f.Action == RepairAction.Aucune ? null : f.Action.ToString(), Annulation.SansObjet);

    // Les clés du scan qui disent elles-mêmes « non lisible » finissent par « Unknown » (Finding.Gpu.ReBarUnknown…).
    public static Constat Depuis(Finding f) => new(
        f.TitleKey, "scan", f.Category, f.Severity,
        f.TitleKey.EndsWith("Unknown", StringComparison.Ordinal) ? EtatConstat.NonLu : EtatConstat.Lu,
        f.TitleKey, null, null, null, null, null, Annulation.SansObjet);

    public static Constat Depuis(BiosFinding f) => new(
        f.TitleKey, "bios", f.Kind.ToString(), f.Severity,
        f.StateNotReadable ? EtatConstat.NonLu : EtatConstat.Lu,
        f.TitleKey, f.CurrentState.ToString(), null, f.RationaleKey, null,
        string.IsNullOrEmpty(f.RecommendedActionKey) ? null : f.RecommendedActionKey,
        // DrDXT n'écrit jamais le BIOS : un réglage BIOS se défait dans le BIOS, par le joueur.
        Annulation.SansObjet);
}
