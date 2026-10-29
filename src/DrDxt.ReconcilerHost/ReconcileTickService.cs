using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost;

// ReconcileTickService : un tour charge une fois le manifeste signé (LoadAsync vérifie la signature d'abord : altération,
// schéma ou E/S remontent en Fail), puis réconcilie. Règle de sécurité (I4) : un chargement raté n'atteint jamais le
// réconciliateur, pas de boucle d'écriture sur un manifeste non fiable ; un diagnostic de sécurité, et le Fail rendu tel
// quel. Aucune licence ; aucun manifeste → état vide, rien d'armé ; un refus ne défait pas les leviers déjà appliqués.
// Le travail privilégié vit dans l'IReconciler injecté : testée avec un store simulé (Ok / Fail) et un substitut.
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
