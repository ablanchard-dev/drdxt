using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// Base commune des leviers de réglage GPU (sous-tension, limite de puissance, courbe de ventilateur).
//
// Comme LatencyTweakBase, chaque levier GPU suit le même contrat de retour : l'OriginalValueJson
// du snapshot est la PrivilegedOp typée inverse, sérialisée, qui porte la vraie origine
// relevée ; annuler = la désérialiser et la renvoyer par IElevatedClient. Le décalage, la
// limite de puissance ou la courbe d'origine s'expriment par une op typée sur liste blanche :
// il n'y a pas de chemin spécial de retour de courbe V/F, le rejeu générique du snapshot suffit.
//
// Un snapshot malformé ou de type inconnu ne doit jamais lever ni lancer une op arbitraire
// → SAFETY.SNAPSHOT_INVALID (échec propre), comme ISafetyManager.RevertAsync.
// -----------------------------------------------------------------------
public abstract class GpuTweakBase : ITweak
{
    protected GpuTweakBase(IElevatedClient elevated) => Elevated = elevated;

    protected IElevatedClient Elevated { get; }

    public abstract string Id { get; }
    public TweakCategory Category => TweakCategory.GpuTuning;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;

    /// <summary>La clé de résumé honnête affichée par l'UI. Jamais une promesse de « +FPS » :
    /// le réglage GPU sur un PC de bureau déjà frais vaut ~0 en FPS de pointe ; le gain est
    /// l'efficacité, la fraîcheur, des 1 % lows plus réguliers.</summary>
    public abstract string SummaryKey { get; }

    public abstract bool AppliesTo(HardwareInventory hw, SettingsState state);

    public abstract Task<Result<TweakSnapshot>> ApplyAsync();

    /// <summary>
    /// Arme l'homme mort du helper élevé pour un décalage ou une limite de puissance GPU
    /// tenus : inscrit un bail dont le retour automatique est la même op inverse typée que
    /// ce levier produit déjà pour <see cref="RevertAsync"/> et, en cas de succès, rend un
    /// <see cref="HeartbeatSender"/> démarré que l'appelant garde en vie tant que le décalage
    /// est tenu.
    ///
    /// Si le contrôleur meurt (ou libère le battement), le bail expire et le helper annule
    /// le levier tout seul : un décalage privilégié tenu ne reste jamais posé sans filet. Tout
    /// passe par IElevatedClient (même pipeline IPC signé, même identité d'appelant) ; l'inverse
    /// voyage en JSON du codec, pour que le helper le relie et le revalide à la feuille près à l'échéance.
    ///
    /// Si le bail ne peut pas être ouvert (pas de helper, refus), ceci échoue proprement et
    /// ne rend aucun battement (ne lève jamais).
    ///
    /// Armé par <see cref="GpuUndervoltTweak.ApplyAvecBailAsync"/>, le chemin du mode Expert,
    /// avant l'écriture. L'autopilote ne l'arme pas : il n'écrit ni fréquence ni tension.
    /// </summary>
    protected async Task<Result<HeartbeatSender>> EstablishAutoRevertLeaseAsync(
        PrivilegedOp inverse, string leaseId, int leaseSeconds)
    {
        var revertJson = PrivilegedOpCodec.Serialize(inverse);
        if (!revertJson.IsOk || revertJson.Value is null)
            return Result<HeartbeatSender>.Fail(ErrorCodes.SnapshotInvalid, "Could not serialize the lease revert op.");

        Result established;
        try
        {
            established = await Elevated
                .ExecuteAsync(new EstablishLeaseOp(leaseId, revertJson.Value, leaseSeconds), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result<HeartbeatSender>.Fail(ErrorCodes.HelperOpFailed, $"Lease establish boundary error: {ex.Message}");
        }

        if (!established.IsOk)
            return Result<HeartbeatSender>.Fail(
                established.Error?.Code ?? ErrorCodes.HelperOpFailed,
                established.Error?.Message ?? "Could not establish the auto-revert lease.");

        var heartbeat = new HeartbeatSender(Elevated, leaseId, leaseSeconds);
        heartbeat.Start();
        return Result<HeartbeatSender>.Ok(heartbeat);
    }

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
