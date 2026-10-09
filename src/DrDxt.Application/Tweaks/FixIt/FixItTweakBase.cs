using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Shared base for the M2 Phase F "fix-it" tweaks (F1 X3D CCD parking, F3 sane
// power plan). Fix-it = REPAIR, not boost (cahier §4 P2.3): each one undoes a
// misconfiguration via a SUPPORTED software route only — never a kernel hack,
// never a BIOS write.
//
// Same revert contract as LatencyTweakBase / GpuTweakBase: the snapshot's
// OriginalValueJson is the serialized INVERSE typed PrivilegedOp carrying the real
// captured original; reverting = deserialize it and send it back through
// IElevatedClient. A malformed / unknown-typed snapshot must never throw and never
// run an arbitrary op → SAFETY.SNAPSHOT_INVALID (fail clean), mirroring
// ISafetyManager.RevertAsync.
//
// HONESTY (§6): a fix-it SummaryKey describes the real effect (repair / less
// stutter), never an unearned "+FPS". The measured gain is PROVEN by the engine's
// re-bench (Phase C2); if nothing is measured, no gain is shown.
//
// (F2, the DDU guide lever, was removed on 08/10/2026: it could never apply, and the doctor already flags an old driver;
// never performs a privileged write — see its own file.)
// -----------------------------------------------------------------------
public abstract class FixItTweakBase : ITweak
{
    /// <summary>Revue n° 14 (08/10/2026) : virtuelle, pour que l'état non lu d'un levier dérivé soit vu par l'interface
    /// (la méthode d'un dérivé sans elle n'était jamais appelée : la valeur par défaut de l'interface répondait).</summary>
    public virtual bool EtatNonLu(SettingsState state) => false;

    protected FixItTweakBase(IElevatedClient elevated) => Elevated = elevated;

    protected IElevatedClient Elevated { get; }

    public abstract string Id { get; }
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;

    /// <summary>The honest summary key surfaced to the UI. Never a "+FPS" claim —
    /// fix-it tweaks repair a misconfiguration (or reduce micro-stutter).</summary>
    public abstract string SummaryKey { get; }

    public abstract bool AppliesTo(HardwareInventory hw, SettingsState state);

    public abstract Task<Result<TweakSnapshot>> ApplyAsync();

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
