using DrDxt.Core;

namespace DrDxt.App.Platform;

/// <summary>
/// Ce qui suit l'annulation d'un levier, que l'annulation vienne du bouton « Annuler » ou du
/// retrait automatique (levier moins bien en jeu). Sans ces deux gestes, le service de réapplication le prendrait pour
/// une dérive (ou le reposerait après une mise à jour de Windows), et le scan suivant dirait « réglage défait, relance
/// l'optimisation » d'une annulation voulue.
/// </summary>
public static class RetraitDeLevier
{
    /// <summary>Échec (helper refusé, manifeste illisible, fichier verrouillé) : le levier reste annulé sur la machine,
    /// mais son entrée reste au manifeste. Rendu à l'appelant, qui le dit.</summary>
    public static async Task<Result> ApresAnnulationAsync(IDesiredStateStore manifeste, DrDxt.Application.Tweaks.JournalDesLeviersAppliques journal, string id)
    {
        // Le manifeste d'abord : c'est lui que le service repose. Retiré du journal d'abord, une App arrêtée entre les deux
        // laissait un levier caché à l'écran et encore reposé après une mise à jour de Windows.
        Result r;
        try
        {
            // Retiré sous ses deux noms : celui de l'App en RevertOnly, celui du service en Tweaks.
            var ids = AppliedTweakManifestMapper.IdDuService(id) is { } service ? new[] { id, service } : new[] { id };
            r = await manifeste.RetirerAsync(ids);
        }
        catch (Exception ex) { r = Result.Fail(ErrorCodes.IoFailure, ex.Message); }
        // Un journal non réécrit ferait dire « défait » au scan suivant ; c'est dit comme le manifeste.
        var journalRetire = journal.Retirer(id);
        if (!r.IsOk || journalRetire) return r;
        return Result.Fail(ErrorCodes.HelperOpFailed, Docteur.T("App.JournalLeviersRetrait", id));
    }
}
