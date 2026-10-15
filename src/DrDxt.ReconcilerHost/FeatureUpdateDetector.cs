using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// FeatureUpdateDetector : le signal principal de déclenchement sur mise à jour de fonctionnalité.
//
// Une mise à jour de fonctionnalité Windows fait monter CurrentBuild.UBR et remet souvent les
// réglages côté OS à leurs valeurs par défaut. Ce détecteur compare le build actuel
// (IOsBuildProbe) au dernier build traité par l'hôte (ReconcilerState.LastFeatureUpdateBuild) et
// dit si une mise à jour de fonctionnalité est arrivée depuis le dernier relevé.
//
// Déclenché sur front, pas sur niveau : sur un vrai changement, il enregistre le nouveau build
// pour que le même build soit sans effet au tour suivant ; la réconciliation est menée une fois
// par mise à jour, pas à chaque interrogation. Un échec de lecture ou d'enregistrement remonte en
// Fail (aucune supposition) : l'hôte ne traite alors pas « inconnu » comme « mis à jour ».
// -----------------------------------------------------------------------
public sealed class FeatureUpdateDetector
{
    private readonly IOsBuildProbe _buildProbe;
    private readonly IReconcilerStateStore _stateStore;

    public FeatureUpdateDetector(IOsBuildProbe buildProbe, IReconcilerStateStore stateStore)
    {
        _buildProbe = buildProbe;
        _stateStore = stateStore;
    }

    /// <summary>Vrai si et seulement si le build actuel de l'OS diffère du dernier enregistré (une
    /// mise à jour de fonctionnalité est arrivée depuis la dernière action de l'hôte). Sur true, il
    /// enregistre le nouveau build (déclenchement sur front). Échec de sonde / de store → Fail.</summary>
    public async Task<Result<bool>> WasFeatureUpdateInstalledAsync(CancellationToken ct)
    {
        var build = _buildProbe.ReadCurrentBuild();
        if (!build.IsOk)
            return Result<bool>.Fail(build.Error!.Value.Code, build.Error.Value.Message);

        var loaded = await _stateStore.LoadAsync(ct);
        if (!loaded.IsOk)
            return Result<bool>.Fail(loaded.Error!.Value.Code, loaded.Error.Value.Message);

        var current = build.Value!;
        var last = loaded.Value!.LastFeatureUpdateBuild;

        // Même build (y compris identique à un build déjà enregistré) → pas de mise à jour, pas d'écriture.
        if (string.Equals(current, last, StringComparison.OrdinalIgnoreCase))
            return Result<bool>.Ok(false);

        // Changé (ou premier lancement avec un relevé vide) → enregistrer le nouveau build pour que le
        // tour suivant avec ce build soit sans effet, puis signaler la mise à jour.
        var saved = await _stateStore.SaveAsync(
            loaded.Value! with { LastFeatureUpdateBuild = current }, ct);
        if (!saved.IsOk)
            return Result<bool>.Fail(saved.Error!.Value.Code, saved.Error.Value.Message);

        return Result<bool>.Ok(true);
    }
}
