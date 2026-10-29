using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Ce que le Gestionnaire de périphériques marque d'un point d'exclamation, et ce que
/// chaque code veut dire. Source des codes et de leurs noms : Microsoft, « Device Manager Error
/// Messages » (learn.microsoft.com/windows-hardware/drivers/install/device-manager-error-messages). Le code decide du
/// statut, pas la presence : sur la machine de reference, les trois peripheriques « en erreur » sont en **code 22**, des
/// profils Bluetooth mains libres de casques desactives (souvent coupes expres, pour que le casque ne bascule pas en
/// qualite telephone).
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
    /// <param name="instanceId">Le PNPDeviceID lu, porté par le constat d'un pilote en échec : c'est lui que le geste
    /// « relancer » vise.</param>
    public static RepairFinding? Juger(string nom, int code, string? instanceId = null) => code switch
    {
        24 or 45 => null,
        22 => new RepairFinding("PERIPHERIQUES", nom, Docteur.T("PE.Desactive"), null, RepairStatus.UserChoiceProbable,
            Docteur.T("PE.DesactiveExplication")),
        29 => new RepairFinding("PERIPHERIQUES", nom, Docteur.T("PE.Bios"), null, RepairStatus.Observed,
            Docteur.T("PE.BiosExplication")),
        14 => new RepairFinding("PERIPHERIQUES", nom, Docteur.T("PE.Redemarrage"), null, RepairStatus.Observed,
            Docteur.T("PE.RedemarrageExplication")),
        _ when PiloteEnPanne.TryGetValue(code, out var probleme) => new RepairFinding("PERIPHERIQUES", nom,
            Docteur.T("PE.Echec", code), Docteur.T("PE.SansErreur"), RepairStatus.Suspicious,
            Docteur.T("PE.EchecExplication", probleme),
            RepairAction.Recommandation, Remede: Docteur.T("PE.Remede", nom), Cible: instanceId),
        _ => new RepairFinding("PERIPHERIQUES", nom, Docteur.T("PE.Code", code), null, RepairStatus.Observed,
            Docteur.T("PE.CodeExplication", code)),
    };
}
