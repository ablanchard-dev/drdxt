using System.Globalization;

namespace DrDxt.Core;

/// <summary>Une tâche planifiée et sa dernière exécution connue (Planificateur de tâches, LastRunTime).</summary>
public sealed record TacheExecutee(string Chemin, DateTimeOffset DerniereExecution);

/// <summary>
/// 07/10/2026 (chantier D4) : les tâches planifiées lancées pendant une partie relevée. L'historique du
/// Planificateur est éteint par défaut dans Windows (journal « TaskScheduler/Operational », lu éteint sur le PC du
/// mainteneur) : seule la DERNIÈRE exécution de chaque tâche est connue, et le constat le dit. Lecture seule ; DrDXT ne
/// désactive aucune tâche (beaucoup sont celles de Windows) et n'active pas l'historique.
/// Mesure du 07/10 : 267 tâches, 4 lancées pendant la partie de JeuB de 01:29 à 03:11, toutes de Microsoft.
/// </summary>
public static class TachesEnPartie
{
    /// <summary>Au plus autant de noms dans le texte ; le reste est compté.</summary>
    public const int NomsMax = 5;

    public static RepairFinding Juger(IReadOnlyList<TacheExecutee>? taches, IReadOnlyList<PlageDeJeu> parties)
    {
        const string Nom = "tâches lancées pendant tes parties";
        if (taches is null)
            return new RepairFinding("TACHES", Nom, null, null, RepairStatus.Unknown,
                "les tâches planifiées n'ont pas pu être lues : DrDXT ne sait pas lesquelles tombent pendant tes parties.");
        if (parties.Count == 0)
            return new RepairFinding("TACHES", Nom, null, null, RepairStatus.Observed,
                "aucune partie relevée : DrDXT ne peut pas encore comparer les tâches planifiées à tes heures de jeu.");

        var pendant = taches.Where(t => parties.Any(p => t.DerniereExecution >= p.Debut && t.DerniereExecution <= p.Fin))
            .OrderByDescending(t => t.DerniereExecution).ToList();
        const string Limite = " Seule la dernière exécution de chaque tâche est connue : l'historique du Planificateur est éteint par "
            + "défaut dans Windows, une tâche qui tourne souvent peut être tombée d'autres fois.";
        if (pendant.Count == 0)
            return new RepairFinding("TACHES", Nom, "aucune", null, RepairStatus.ExpectedDefault,
                string.Format(CultureInfo.InvariantCulture, "aucune des {0} tâches planifiées ne s'est lancée pour la dernière fois pendant une "
                    + "de tes {1} parties relevées.", taches.Count, parties.Count) + Limite);

        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var noms = string.Join(", ", pendant.Take(NomsMax).Select(t =>
            $"{t.Chemin} ({t.DerniereExecution.ToLocalTime().ToString("dd/MM HH:mm", fr)})"));
        var reste = pendant.Count > NomsMax ? string.Format(CultureInfo.InvariantCulture, " et {0} autre(s)", pendant.Count - NomsMax) : "";
        return new RepairFinding("TACHES", Nom, string.Format(CultureInfo.InvariantCulture, "{0} pendant une partie", pendant.Count), null,
            RepairStatus.Observed,
            string.Format(CultureInfo.InvariantCulture, "{0} tâche(s) planifiée(s) se sont lancées pendant une partie : {1}{2}.",
                pendant.Count, noms, reste)
            + Limite + " DrDXT ne désactive aucune tâche : la plupart sont celles de Windows.");
    }
}
