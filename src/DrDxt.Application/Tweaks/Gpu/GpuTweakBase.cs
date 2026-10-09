using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// Shared base for the M2 Phase E GPU-tuning tweaks (E2 undervolt, E3 power-limit,
// E4 fan-curve).
//
// Like LatencyTweakBase, every GPU tweak follows the same revert contract: the
// snapshot's OriginalValueJson is the serialized INVERSE typed PrivilegedOp
// carrying the real captured original; reverting = deserialize it and send it back
// through IElevatedClient. Under Option A (master §11) the original offset / power
// limit / fan curve IS expressible as a typed allowlisted op, so there is NO
// special V/F-curve revert path — the generic snapshot-replay is enough.
//
// A malformed / unknown-typed snapshot must never throw and never run an arbitrary
// op → SAFETY.SNAPSHOT_INVALID (fail clean), mirroring ISafetyManager.RevertAsync.
// -----------------------------------------------------------------------
public abstract class GpuTweakBase : ITweak
{
    protected GpuTweakBase(IElevatedClient elevated) => Elevated = elevated;

    protected IElevatedClient Elevated { get; }

    public abstract string Id { get; }
    public TweakCategory Category => TweakCategory.GpuTuning;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;

    /// <summary>The honest summary key surfaced to the UI. Never a "+FPS" claim:
    /// GPU tuning on an already-cool desktop is ~0 on peak FPS — the win is
    /// efficiency / cooler / steadier 1% lows (master §5).</summary>
    public abstract string SummaryKey { get; }

    public abstract bool AppliesTo(HardwareInventory hw, SettingsState state);

    public abstract Task<Result<TweakSnapshot>> ApplyAsync();

    /// <summary>
    /// Arms the elevated helper's dead-man's-switch for a HELD GPU offset / power
    /// limit: registers a lease whose recorded auto-revert is the same typed inverse
    /// op this tweak already produces for <see cref="RevertAsync"/>, and — on success
    /// — returns a STARTED <see cref="HeartbeatSender"/> the caller keeps alive for as
    /// long as the offset is held.
    ///
    /// If the controller dies (or disposes the heartbeat) the lease expires and the
    /// helper reverts the held tweak automatically — so a held privileged offset is
    /// never left applied with no safety net. Everything goes through IElevatedClient
    /// (the same signed IPC pipeline + caller identity); the inverse is carried as
    /// codec JSON so the helper re-binds + re-validates it leaf-exact at expiry.
    ///
    /// If the lease cannot be established (no helper, refused), this fails clean and
    /// returns NO heartbeat (never throws).
    ///
    /// 08/10/2026 (GPU-OC) : armé par <see cref="GpuUndervoltTweak.ApplyAvecBailAsync"/>, le chemin du mode Expert,
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

    /// <summary>Reverts by replaying the serialized inverse op from the snapshot.
    /// Defensive: malformed snapshot → SAFETY.SNAPSHOT_INVALID, never throws.</summary>
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
