using DrDxt.Core;

namespace DrDxt.App.Platform;

/// <summary>
/// 04/10/2026 (plan 6.1) : ce qui suit l'annulation d'un levier, que l'annulation vienne du bouton « Annuler » ou du
/// retrait automatique (levier moins bien en jeu). Sans ces deux gestes, le service de réapplication le signalait comme
/// une dérive (ou le reposait après une mise à jour de Windows), et le scan suivant disait « réglage défait, relance
/// l'optimisation » d'une annulation voulue.
/// </summary>
public static class RetraitDeLevier
{
    /// <summary>Échec (helper refusé, manifeste illisible, fichier verrouillé) : le levier reste annulé sur la machine,
    /// mais son entrée reste au manifeste. Rendu à l'appelant, qui le dit (revue du 06/10 : c'était avalé).</summary>
    public static async Task<Result> ApresAnnulationAsync(IDesiredStateStore manifeste, DrDxt.Application.Tweaks.JournalDesLeviersAppliques journal, string id)
    {
        journal.Retirer(id);
        try
        {
            // 06/10/2026 (revue) : retiré sous ses deux noms (celui de l'App en RevertOnly, celui du service en
            // Tweaks). Avant, il cherchait l'un parmi l'autre et renvoyait un manifeste que le magasin fusionnait :
            // rien n'était jamais retiré.
            var ids = AppliedTweakManifestMapper.IdDuService(id) is { } service ? new[] { id, service } : new[] { id };
            return await manifeste.RetirerAsync(ids);
        }
        catch (Exception ex) { return Result.Fail(ErrorCodes.IoFailure, ex.Message); }
    }
}
