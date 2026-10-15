using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Base commune des leviers de réparation (parcage des CCD X3D, plan d'alimentation sain).
// Réparer, pas booster : chacun défait une erreur de réglage par une voie logicielle prise
// en charge seulement, jamais par un bricolage du noyau, jamais par une écriture du BIOS.
//
// Même contrat de retour que LatencyTweakBase / GpuTweakBase : l'OriginalValueJson du
// snapshot est la PrivilegedOp typée inverse, sérialisée, qui porte la vraie origine
// relevée ; annuler = la désérialiser et la renvoyer par IElevatedClient. Un snapshot
// malformé ou de type inconnu ne doit jamais lever ni lancer une op arbitraire
// → SAFETY.SNAPSHOT_INVALID (échec propre), comme ISafetyManager.RevertAsync.
//
// Honnêteté : le SummaryKey d'une réparation décrit l'effet réel (réparation, moins
// d'à-coups), jamais un « +FPS » non mérité. Le gain mesuré est prouvé par la remesure du
// moteur (Phase C2) ; sans mesure, aucun gain n'est affiché.
// -----------------------------------------------------------------------
public abstract class FixItTweakBase : ITweak
{
    /// <summary>Virtuelle, pour que l'état non lu d'un levier dérivé soit vu par l'interface
    /// (la méthode d'un dérivé sans elle n'était jamais appelée : la valeur par défaut de l'interface répondait).</summary>
    public virtual bool EtatNonLu(SettingsState state) => false;

    /// <summary>Virtuelle pour la même raison : un réglage relu par Windows au démarrage le déclare.</summary>
    public virtual bool TakesEffectAfterReboot => false;

    protected FixItTweakBase(IElevatedClient elevated) => Elevated = elevated;

    protected IElevatedClient Elevated { get; }

    public abstract string Id { get; }
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;

    /// <summary>La clé de résumé honnête affichée par l'UI. Jamais une promesse de « +FPS » :
    /// les réparations corrigent une erreur de réglage (ou réduisent les micro-saccades).</summary>
    public abstract string SummaryKey { get; }

    public abstract bool AppliesTo(HardwareInventory hw, SettingsState state);

    public abstract Task<Result<TweakSnapshot>> ApplyAsync();

    /// <summary>Annule en rejouant l'op inverse sérialisée du snapshot.
    /// Prudent : snapshot malformé → SAFETY.SNAPSHOT_INVALID, ne lève jamais.</summary>
    public virtual async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        Result<PrivilegedOp> decoded;
        try
        {
            decoded = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SnapshotInvalid, $"Snapshot decode error: {ex.Message}");
        }

        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid,
                "Snapshot original value is not a known typed privileged op.");

        try
        {
            return await Elevated.ExecuteAsync(decoded.Value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }
}
