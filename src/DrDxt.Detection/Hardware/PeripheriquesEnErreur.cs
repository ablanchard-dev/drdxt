using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// 25/09/2026 — ce que le Gestionnaire de périphériques marque d'un point d'exclamation, et ce que
/// chaque code veut dire. Source des codes et de leurs noms : Microsoft, « Device Manager Error
/// Messages » (learn.microsoft.com/windows-hardware/drivers/install/device-manager-error-messages).
///
/// Ce qui a decide du tri, mesure le meme jour sur la machine de reference : ses trois peripheriques
/// « en erreur » sont en **code 22**, des profils Bluetooth mains libres de casques, DESACTIVES.
/// Beaucoup de joueurs les coupent exprès pour que le casque ne bascule pas en qualite telephone.
/// Un docteur qui les appellerait pannes mentirait : le code decide du statut, pas sa presence.
/// </summary>
public static class PeripheriquesEnErreur
{
    public const string Source = "learn.microsoft.com/windows-hardware/drivers/install/device-manager-error-messages";

    /// <summary>Les codes ou le pilote ne fonctionne pas : le peripherique est inutilisable.</summary>
    private static readonly Dictionary<int, string> PiloteEnPanne = new()
    {
        [1] = "CM_PROB_NOT_CONFIGURED", [10] = "CM_PROB_FAILED_START", [18] = "CM_PROB_REINSTALL",
        [19] = "CM_PROB_REGISTRY", [28] = "CM_PROB_FAILED_INSTALL", [31] = "CM_PROB_FAILED_ADD",
        [37] = "CM_PROB_FAILED_DRIVER_ENTRY", [39] = "CM_PROB_DRIVER_FAILED_LOAD",
        [43] = "CM_PROB_FAILED_POST_START", [48] = "CM_PROB_DRIVER_BLOCKED", [52] = "CM_PROB_UNSIGNED_DRIVER",
    };

    /// <summary>Un constat pour un peripherique en erreur, ou <c>null</c> quand il n'est pas
    /// branche (codes 24 et 45 : Windows garde la trace d'un appareil absent, rien a soigner).</summary>
    public static RepairFinding? Juger(string nom, int code) => code switch
    {
        24 or 45 => null,
        22 => new RepairFinding("PERIPHERIQUES", nom, "désactivé (code 22)", null, RepairStatus.UserChoiceProbable,
            "désactivé dans le Gestionnaire de périphériques (CM_PROB_DISABLED). C'est souvent voulu, "
            + "par exemple le profil mains libres d'un casque Bluetooth : ce n'est pas une panne."),
        29 => new RepairFinding("PERIPHERIQUES", nom, "désactivé par le BIOS (code 29)", null, RepairStatus.Observed,
            "désactivé par le firmware de la carte mère (CM_PROB_HARDWARE_DISABLED) : il se réactive dans le BIOS."),
        14 => new RepairFinding("PERIPHERIQUES", nom, "attend un redémarrage (code 14)", null, RepairStatus.Observed,
            "Windows a besoin d'un redémarrage pour finir de l'installer (CM_PROB_NEED_RESTART)."),
        _ when PiloteEnPanne.TryGetValue(code, out var probleme) => new RepairFinding("PERIPHERIQUES", nom,
            $"pilote en échec (code {code})", "sans erreur", RepairStatus.Suspicious,
            $"son pilote ne fonctionne pas ({probleme}) : le périphérique est inutilisable tant qu'il n'est "
            + "pas réinstallé ou mis à jour. Le Gestionnaire de périphériques affiche le même code.",
            RepairAction.Recommandation),
        _ => new RepairFinding("PERIPHERIQUES", nom, $"code {code}", null, RepairStatus.Observed,
            $"le Gestionnaire de périphériques signale le code {code}. Sa signification est dans la liste "
            + "Microsoft citée en source ; DrDXT ne l'interprète pas."),
    };
}
