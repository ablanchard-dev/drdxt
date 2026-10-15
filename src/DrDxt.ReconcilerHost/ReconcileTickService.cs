using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// ReconcileTickService : une passe de réconciliation, en échec propre sur un mauvais manifeste.
//
// Un « tour » : charger le manifeste d'état voulu signé, une seule fois, puis réconcilier. Le
// LoadAsync du store vérifie la signature avant de rendre la main : une
// altération, une erreur de schéma ou d'E/S remonte donc ici en Fail. La règle critique de
// sécurité (I4) : un chargement raté n'atteint jamais le réconciliateur ; on n'entre pas dans une
// boucle d'écriture sur un manifeste auquel on ne peut pas se fier. On journalise à la place un
// diagnostic de sécurité et on rend le Fail tel quel.
//
// Aucune licence : le manifeste s'applique tel quel après vérification de sa signature. Aucun manifeste
// → état vide (rien d'armé). Un refus ne défait pas automatiquement les leviers déjà appliqués.
//
// Tout le travail privilégié vit dans l'IReconciler injecté ; cette classe ne fait que séquencer
// chargement → réconciliation et imposer l'échec propre : elle se teste entièrement avec un store
// simulé (Ok / Fail) et un réconciliateur de substitution.
// -----------------------------------------------------------------------
public sealed class ReconcileTickService
{
    private readonly IDesiredStateStore _store;
    private readonly IReconciler _reconciler;
    private readonly IDiagnosticsLog _diagnostics;

    public ReconcileTickService(
        IDesiredStateStore store,
        IReconciler reconciler,
        IDiagnosticsLog diagnostics)
    {
        _store = store;
        _reconciler = reconciler;
        _diagnostics = diagnostics;
    }

    /// <summary>Charge le manifeste vérifié et le réconcilie. Un chargement raté (altération / schéma / E/S)
    /// est journalisé comme événement de sécurité et rendu sans réconcilier : jamais de boucle d'écriture sur
    /// un manifeste non fiable (fail-closed, I4).</summary>
    public async Task<Result> RunTickAsync(CancellationToken ct)
    {
        var loaded = await _store.LoadAsync();
        if (!loaded.IsOk)
        {
            var error = loaded.Error!.Value;
            // Journal de sécurité : l'acteur marque ceci comme événement de sécurité pour le journal
            // d'audit. On n'appelle exprès pas le réconciliateur (échec propre, I4).
            _diagnostics.Change(
                tweakId: HostActors.ManifestTweakId,
                from: error.Code,
                to: "reconcile-skipped",
                actor: HostActors.Security);
            return Result.Fail(error.Code, error.Message);
        }

        return await _reconciler.ReconcileAsync(loaded.Value!, ct);
    }
}
