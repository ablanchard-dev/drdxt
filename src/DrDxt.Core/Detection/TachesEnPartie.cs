using System.Globalization;

namespace DrDxt.Core;

/// <summary>Une tâche planifiée et sa dernière exécution connue (Planificateur de tâches, LastRunTime).</summary>
public sealed record TacheExecutee(string Chemin, DateTimeOffset DerniereExecution);

/// <summary>
/// Les tâches planifiées lancées pendant une partie relevée. L'historique du
/// Planificateur est éteint par défaut dans Windows (journal « TaskScheduler/Operational », lu éteint sur la machine
/// de référence) : seule la dernière exécution de chaque tâche est connue, et le constat le dit. Lecture seule ; DrDXT ne
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
                Docteur.T("TP.NonLues"));
        if (parties.Count == 0)
            return new RepairFinding("TACHES", Nom, null, null, RepairStatus.Observed,
                Docteur.T("TP.SansPartie"));

        var pendant = taches.Where(t => parties.Any(p => t.DerniereExecution >= p.Debut && t.DerniereExecution <= p.Fin))
            .OrderByDescending(t => t.DerniereExecution).ToList();
        var Limite = Docteur.T("TP.Limite");
        if (pendant.Count == 0)
            return new RepairFinding("TACHES", Nom, Docteur.T("TP.Aucune"), null, RepairStatus.ExpectedDefault,
                Docteur.T("TP.AucuneDes", taches.Count, parties.Count) + Limite);

        var format = Docteur.T("TP.Date");
        var noms = string.Join(", ", pendant.Take(NomsMax).Select(t =>
            $"{t.Chemin} ({t.DerniereExecution.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)})"));
        var reste = pendant.Count > NomsMax ? Docteur.T("TP.EtAutres", pendant.Count - NomsMax) : "";
        return new RepairFinding("TACHES", Nom, Docteur.T("TP.Pendant", pendant.Count), null,
            RepairStatus.Observed,
            Docteur.T("TP.Liste", pendant.Count, noms, reste)
            + Limite + Docteur.T("TP.Desactive"));
    }
}
