using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Ce que `CrashDumpEnabled` permet de capturer — pas ce qu'il « devrait » valoir.
///
/// Deux constats se contredisaient dans le meme rapport, decouvert le 08/09/2026 en lisant la
/// sortie. Une section « CRASH DUMP » declarait `7 (automatique)` comme « le defaut Windows »,
/// classait `3` en `Suspicious` et RECOMMANDAIT d'y toucher. Une section « STABILITE », ecrite le
/// meme jour sans voir la premiere, rendait `Observed` en disant explicitement que le defaut
/// Windows n'avait pas ete etabli sur source.
///
/// La seconde avait raison. Verification faite sur
/// `learn.microsoft.com/windows/client-management/generate-kernel-or-complete-crash-dump` : la
/// page decrit les types de vidage et **ne publie ni valeur numerique ni defaut**. La premiere
/// version recommandait donc un geste sur une norme inventee — et, pire, un geste que le rapport
/// prouve INUTILE sur cette machine, ou c'est la CREATION du vidage qui echoue et non son type.
///
/// Ce qui reste affirmable sans defaut : ce que chaque mode CAPTURE. « Aucun vidage n'est
/// ecrit » est un fait de capacite, pas une comparaison — et c'est le seul cas qui merite une
/// recommandation.
/// </summary>
public static class CrashDumpPolicy
{
    /// <summary>Juge la valeur brute de `CrashDumpEnabled`. <c>null</c> = non lue.</summary>
    public static RepairFinding Juger(int? valeur) => valeur switch
    {
        null => new RepairFinding("STABILITE", "type de vidage", null, null, RepairStatus.Unknown,
            "CrashControl illisible"),

        // Le seul cas qui merite une recommandation, et il ne compare rien : aucun vidage n'est
        // ecrit, donc aucun plantage futur ne laissera de trace. C'est un fait de capacite.
        0 => new RepairFinding("STABILITE", "type de vidage", "0 (aucun)", null,
            RepairStatus.Suspicious,
            Action: RepairAction.Recommandation,
            Explication:
            "aucun vidage n'est produit : un plantage ne laissera aucune trace exploitable, et "
            + "chaque incident suivant sera perdu pour l'enquête. C'est une valeur que les scripts "
            + "d'optimisation posent pour économiser du disque."),

        1 => Capture("1 (complet)", "toute la mémoire physique"),
        2 => Capture("2 (noyau)", "la mémoire du noyau : les piles des pilotes y sont"),
        3 => Capture("3 (petit vidage)",
            "environ 256 Ko : de quoi nommer le code d'arrêt, pas d'attribuer la cause à un pilote"),
        7 => Capture("7 (automatique)",
            "comme le vidage noyau, en laissant Windows dimensionner le fichier d'échange"),

        var v => Capture($"{v} (mode inconnu)", "ce mode n'est pas dans la table de DrDXT"),
    };

    /// <summary>
    /// Tous les modes qui ECRIVENT quelque chose partagent le meme traitement : on dit ce qu'ils
    /// capturent, et rien de plus.
    ///
    /// `Observed` et jamais `NonDefault` ni `ExpectedDefault` : les deux affirmeraient connaitre
    /// la valeur d'origine, que Microsoft ne publie pas. Savoir ce qu'un mode CAPTURE ne dit rien
    /// de qui l'a choisi.
    /// </summary>
    private static RepairFinding Capture(string valeur, string quoi) =>
        new("STABILITE", "type de vidage", valeur, null, RepairStatus.Observed,
            $"capture {quoi}. Aucune comparaison à un défaut : la documentation Microsoft décrit "
            + "les types de vidage sans publier de valeur par défaut, donc rien ici ne dit que "
            + "quelqu'un l'a changé.");
}
