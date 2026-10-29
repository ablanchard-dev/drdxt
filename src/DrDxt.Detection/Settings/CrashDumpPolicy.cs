using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Ce que `CrashDumpEnabled` permet de capturer, pas ce qu'il « devrait » valoir : la page source
/// `learn.microsoft.com/windows/client-management/generate-kernel-or-complete-crash-dump` decrit les types de vidage sans
/// valeur numerique ni defaut, et `7 (automatique)` comme « defaut Windows » serait une norme inventee (changer le type
/// ne sert a rien quand c'est la creation qui echoue). « Aucun vidage n'est ecrit », un fait de capacite, est le seul cas
/// qui merite une recommandation.
/// </summary>
public static class CrashDumpPolicy
{
    /// <summary>Juge la valeur brute de `CrashDumpEnabled`. <c>null</c> = non lue.</summary>
    public static RepairFinding Juger(int? valeur) => valeur switch
    {
        null => new RepairFinding("STABILITE", "type de vidage", null, null, RepairStatus.Unknown,
            Docteur.T("CD.Illisible")),

        // Le seul cas qui merite une recommandation : aucun vidage, donc aucune trace d'un plantage futur.
        0 => new RepairFinding("STABILITE", "type de vidage", Docteur.T("CD.Aucun"), null,
            RepairStatus.Suspicious,
            Action: RepairAction.Recommandation,
            Explication:
            Docteur.T("CD.AucunExplication"),
            Remede: Docteur.T("CD.Remede")),

        1 => Capture(Docteur.T("CD.Complet"), Docteur.T("CD.CompletQuoi")),
        2 => Capture(Docteur.T("CD.Noyau"), Docteur.T("CD.NoyauQuoi")),
        3 => Capture(Docteur.T("CD.Petit"), Docteur.T("CD.PetitQuoi")),
        7 => Capture(Docteur.T("CD.Auto"), Docteur.T("CD.AutoQuoi")),

        var v => Capture(Docteur.T("CD.Inconnu", v), Docteur.T("CD.InconnuQuoi")),
    };

    /// <summary>Les modes qui ecrivent quelque chose : on dit ce qu'ils capturent, rien de plus. `Observed`, jamais
    /// `NonDefault` ni `ExpectedDefault`, qui affirmeraient une valeur d'origine que Microsoft ne publie pas.</summary>
    private static RepairFinding Capture(string valeur, string quoi) =>
        new("STABILITE", "type de vidage", valeur, null, RepairStatus.Observed,
            Docteur.T("CD.Capture", quoi));
}
