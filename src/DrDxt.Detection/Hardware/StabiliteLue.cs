using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Ce que la stabilite de la machine interdit, lu pour le selecteur. Remonte de
/// `tools/candidatprobe` : l'outil et l'App lisent les memes incidents de la meme facon.
/// Un echec de lecture rend <c>null</c> : « pas lu » n'est pas « aucun incident ».
/// </summary>
public static class StabiliteLue
{
    /// <summary>L'instabilite graphique deja repetee. On groupe par (code, parametre 1) : c'est la
    /// repetition d'une meme signature qui distingue une condition d'un accident.</summary>
    public static InstabiliteGpuObservee? InstabiliteGpu(ILiveKernelReportLog log)
    {
        var lecture = log.Lire();
        if (!lecture.IsOk || lecture.Value is not { } rapports) return null;

        var groupe = rapports
            .Where(r => r.EstGraphique)
            .GroupBy(r => (r.Code, r.Parametre1))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return groupe is null
            ? null
            : new InstabiliteGpuObservee(groupe.Count(), groupe.Max(r => r.Quand), groupe.First().Phrase);
    }

    /// <summary>Les vidages à chaud du noyau graphique, datés, pour la garde d'incident. Non lu : null.</summary>
    public static IReadOnlyList<(DateTimeOffset Quand, int Code)>? VidagesCarte(ILiveKernelReportLog log)
        => log.Lire() is { IsOk: true, Value: { } rapports }
            ? rapports.Where(r => r.EstGraphique).Select(r => (r.Quand, (int)r.Code)).ToList()
            : null;

    /// <summary>Les arrets brutaux sur la fenetre du veto, hors appui long sur le bouton, chacun avec sa famille
    /// (06/10/2026 : une coupure en veille ne ferme rien, un ecran bleu graphique ferme la carte).</summary>
    public static IReadOnlyList<ArretClasse>? ArretsBrutaux(IUnexpectedShutdownLog log, DateTimeOffset maintenant)
        => log.Detail(maintenant - CandidateSelector.FenetreInstabilite) is { IsOk: true } arrets
            ? ArretBrutal.HorsBouton(arrets.Value!).Select(a => a.Classe).ToList()
            : null;
}
