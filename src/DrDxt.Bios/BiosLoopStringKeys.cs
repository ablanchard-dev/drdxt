using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Clés resx du résultat de la boucle fermée après redémarrage : les libellés honnêtes « c'est
/// pris » / « ce n'est pas pris » / « impossible à vérifier » et la carte de récupération montrée
/// quand un changement firmware n'a pas pris ou n'a pas pu être vérifié. FR et EN vivent dans la
/// resx partagée d'Infrastructure ; le vérificateur n'émet que ces clés, jamais de texte
/// d'interface en dur (comme <c>BiosStringKeys</c> / <c>BiosGuideStringKeys</c> / <c>ErrorCodes</c>).
/// </summary>
public static class BiosLoopStringKeys
{
    // ── Per-setting verdict labels (honest constat). ──
    public const string VerdictTook = "Bios.Loop.Verdict.Took";
    public const string VerdictDidNotTake = "Bios.Loop.Verdict.DidNotTake";
    public const string VerdictUnreadable = "Bios.Loop.Verdict.Unreadable";

    // ── Clean-success header (every setting took). ──
    public const string AllTook = "Bios.Loop.AllTook";

    // ── Nom lisible d'un reglage, pour la ligne « <reglage> : <verdict> ». Le verifieur rend
    //    un FirmwareSettingKind ; l'afficher brut mettrait du texte non traduit a l'ecran, et
    //    le mapper en dur dans le VM contournerait la discipline « aucun texte code en dur ». ──
    public const string SettingNamePrefix = "Bios.Loop.Setting.";

    /// <summary>La cle du nom lisible d'un reglage. Ferme par construction : le nom du membre
    /// de l'enumeration est le suffixe, donc un nouveau reglage sans sa cle se voit tout de
    /// suite a l'ecran au lieu de tomber silencieusement sur un libelle generique.</summary>
    public static string SettingName(FirmwareSettingKind kind) => SettingNamePrefix + kind;

    // ── Carte de récupération (montrée seulement quand quelque chose n'a pas pris ou est illisible).
    //    L'ordre ci-dessous est l'ordre d'affichage de la carte. ──
    public const string RecoveryHeader = "Bios.Loop.Recovery.Header";
    public const string RecoveryReenter = "Bios.Loop.Recovery.Reenter";
    public const string RecoveryOneChangeAtATime = "Bios.Loop.Recovery.OneChangeAtATime";
    public const string RecoveryLoadDefaults = "Bios.Loop.Recovery.LoadDefaults";
    public const string RecoveryClearCmos = "Bios.Loop.Recovery.ClearCmos";

    /// <summary>Les clés de la carte de récupération, dans l'ordre d'affichage (en-tête d'abord). Source unique.</summary>
    public static readonly IReadOnlyList<string> RecoveryCard = new[]
    {
        RecoveryHeader,
        RecoveryReenter,
        RecoveryOneChangeAtATime,
        RecoveryLoadDefaults,
        RecoveryClearCmos,
    };
}
