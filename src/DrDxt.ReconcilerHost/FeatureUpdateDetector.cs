using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// FeatureUpdateDetector (plan T27) — the PRIMARY feature-update trigger signal.
//
// A Windows feature update bumps CurrentBuild.UBR and frequently resets OS-side
// settings to defaults. This detector compares the current build (IOsBuildProbe)
// against the last build the host acted on (ReconcilerState.LastFeatureUpdateBuild)
// and reports whether a feature update has landed SINCE the last record.
//
// EDGE-TRIGGERED, not level: on a genuine change it persists the new build so the
// Same build is a no-op on the next tick — the reconcile is driven once per update,
// not on every poll. A read or persist failure is surfaced as Fail (no guess): the
// host then does not treat "unknown" as "updated".
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

    /// <summary>True iff the current OS build differs from the last recorded one (a
    /// feature update landed since the host last acted). On true it persists the new
    /// build (edge-trigger). Probe/store failure → Fail.</summary>
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

        // Same build (including: same as a previously-recorded build) → no update, no write.
        if (string.Equals(current, last, StringComparison.OrdinalIgnoreCase))
            return Result<bool>.Ok(false);

        // Changed (or first run with an empty record) → record the new build so the next
        // tick with this build is a no-op, then report the update.
        var saved = await _stateStore.SaveAsync(
            loaded.Value! with { LastFeatureUpdateBuild = current }, ct);
        if (!saved.IsOk)
            return Result<bool>.Fail(saved.Error!.Value.Code, saved.Error.Value.Message);

        return Result<bool>.Ok(true);
    }
}
