using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Ce que `CrashDumpEnabled` permet de capturer, pas ce qu'il « devrait » valoir.
///
/// Microsoft ne publie pas de defaut : la page source
/// `learn.microsoft.com/windows/client-management/generate-kernel-or-complete-crash-dump` decrit les
/// types de vidage sans valeur numerique ni defaut. Traiter `7 (automatique)` comme « le defaut Windows »
/// et recommander de changer `3` reposerait sur une norme inventee ; et quand c'est la creation du vidage
/// qui echoue, changer son type ne sert a rien.
///
/// Ce qui reste affirmable sans defaut : ce que chaque mode capture. « Aucun vidage n'est
/// ecrit » est un fait de capacite, pas une comparaison, et c'est le seul cas qui merite une
/// recommandation.
/// </summary>
public static class CrashDumpPolicy
{
    /// <summary>Juge la valeur brute de `CrashDumpEnabled`. <c>null</c> = non lue.</summary>
    public static RepairFinding Juger(int? valeur) => valeur switch
    {
        null => new RepairFinding("STABILITE", "type de vidage", null, null, RepairStatus.Unknown,
            Docteur.T("CD.Illisible")),

        // Le seul cas qui merite une recommandation, et il ne compare rien : aucun vidage n'est
        // ecrit, donc aucun plantage futur ne laissera de trace. C'est un fait de capacite.
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

    /// <summary>
    /// Tous les modes qui ecrivent quelque chose partagent le meme traitement : on dit ce qu'ils
    /// capturent, et rien de plus.
    ///
    /// `Observed` et jamais `NonDefault` ni `ExpectedDefault` : les deux affirmeraient connaitre
    /// la valeur d'origine, que Microsoft ne publie pas. Savoir ce qu'un mode capture ne dit rien
    /// de qui l'a choisi.
    /// </summary>
    private static RepairFinding Capture(string valeur, string quoi) =>
        new("STABILITE", "type de vidage", valeur, null, RepairStatus.Observed,
            Docteur.T("CD.Capture", quoi));
}
