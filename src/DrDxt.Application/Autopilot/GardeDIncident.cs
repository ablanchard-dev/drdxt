using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>
/// Des arrêts brutaux
/// passés ne refusent plus Optimiser. Le filet les remplace : un réglage posé par DrDXT et suivi d'un arrêt brutal hors
/// veille dans la semaine est rendu seul, au lancement suivant. L'incident n'est plus « inattribuable » : ce qui a été
/// posé juste avant est retiré, et la suite dit si l'incident revient sans lui.
/// </summary>
public static class GardeDIncident
{
    public static readonly TimeSpan Surveillance = TimeSpan.FromDays(7);

    /// <summary>Un réglage qui n'écrit que la session (mode Jeu, préférences HKCU) ou le moment où
    /// Windows Update redémarre ne peut pas causer un arrêt brutal ; une coupure de courant ne le rend pas. Un levier qui ne
    /// déclare pas sa surface reste sous garde.</summary>
    public static bool PeutCauserUnArret(string id) =>
        CatalogueDeCandidats.Decrire(id)?.Surface is not (SurfaceTouchee.OsUtilisateur or SurfaceTouchee.PlanificationWindows
            or SurfaceTouchee.Aucune);

    /// <summary>Les réglages à rendre, avec l'arrêt qui les fait rendre (le premier après la pose). Une coupure pendant la
    /// veille ne dit rien de la stabilité en charge : elle ne rend rien.</summary>
    /// <param name="vidagesCarte">Les vidages à chaud du noyau graphique (WER, codes 0x113/0x116/0x117/
    /// 0x141/0x193), datés. Un vidage dans la semaine après la pose d'un réglage de la carte ou du pilote le rend : la machine
    /// a continué de tourner, mais le pilote a décroché après notre écriture, et l'attribution appartient au joueur, pas à un refus.</param>
    public static IReadOnlyList<(string Id, ArretClasse Arret, bool VidageAChaud)> ARendre(
        IEnumerable<(string Id, DateTimeOffset Pose)> poses, IReadOnlyList<ArretClasse>? arrets,
        IReadOnlyList<(DateTimeOffset Quand, int Code)>? vidagesCarte = null)
    {
        var liste = poses.ToList();
        var rendus = new List<(string Id, ArretClasse Arret, bool VidageAChaud)>();
        if (arrets is { Count: > 0 })
            rendus.AddRange(liste
                .Where(p => PeutCauserUnArret(p.Id))
                .Select(p => (p.Id, Arret: arrets
                    .Where(a => a.Famille != FamilleDArret.Veille && a.Quand > p.Pose && a.Quand - p.Pose <= Surveillance)
                    .OrderBy(a => a.Quand).FirstOrDefault()))
                .Where(x => x.Arret is not null)
                .Select(x => (x.Id, x.Arret!, false)));
        if (vidagesCarte is { Count: > 0 })
            rendus.AddRange(liste
                .Where(p => EcritLaCarte(p.Id) && rendus.All(r => r.Id != p.Id))
                .Select(p => (p.Id, Vidage: vidagesCarte
                    .Where(v => v.Quand > p.Pose && v.Quand - p.Pose <= Surveillance)
                    .OrderBy(v => v.Quand).Select(v => new ArretClasse(v.Quand, ClasseurDArrets.Classer(v.Code, false), v.Code)).FirstOrDefault()))
                .Where(x => x.Vidage is not null)
                .Select(x => (x.Id, x.Vidage!, true)));
        return rendus;
    }

    /// <summary>Les réglages qu'un décrochage du pilote graphique peut mettre en cause : la carte et le pilote.</summary>
    public static bool EcritLaCarte(string id) =>
        CatalogueDeCandidats.Decrire(id)?.Surface is SurfaceTouchee.CarteGraphique or SurfaceTouchee.PiloteGraphique;
}
