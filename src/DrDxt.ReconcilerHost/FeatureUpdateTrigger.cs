using DrDxt.Core;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// IUpdateSignal (plan T28) — a corroborating "a Windows update/setup just completed"
// signal, behind a tiny seam so the trigger COMPOSITION is unit-tested while the real
// event-log read stays a thin, separately-smoke-tested edge.
// -----------------------------------------------------------------------

/// <summary>Reads a corroborating update signal — typically a recent Windows
/// Update / Setup completion in the event log. CORROBORATING only: it may fire
/// earlier than the build flips, but the build detector is the primary authority.
/// Unreadable → Fail (the trigger treats a corroborating failure as best-effort, see
/// FeatureUpdateTrigger).</summary>
public interface IUpdateSignal
{
    Task<Result<bool>> RecentSetupCompletionAsync(CancellationToken ct);
}

// -----------------------------------------------------------------------
// FeatureUpdateTrigger — composes the PRIMARY build detector (T27) with the
// CORROBORATING event signal (T28).
//
// Fires when EITHER says "update". The primary is authoritative: if the build read
// FAILS the trigger fails (an unknown build must not be silently treated as "no
// update"). The corroborating read is BEST-EFFORT: a failure there never masks a
// proven primary update and never fabricates one — it only ADDS an early fire when it
// can be read and says "yes".
// -----------------------------------------------------------------------
public sealed class FeatureUpdateTrigger
{
    private readonly FeatureUpdateDetector _buildDetector;
    private readonly IUpdateSignal _eventSignal;

    public FeatureUpdateTrigger(FeatureUpdateDetector buildDetector, IUpdateSignal eventSignal)
    {
        _buildDetector = buildDetector;
        _eventSignal = eventSignal;
    }

    /// <summary>True iff a reconcile should run because a feature update is detected by
    /// the primary build signal OR corroborated by the event signal. Primary read
    /// failure → Fail.</summary>
    public async Task<Result<bool>> ShouldReconcileAsync(CancellationToken ct)
    {
        // PRIMARY (authoritative). A failure here is a real failure, not "no update".
        var primary = await _buildDetector.WasFeatureUpdateInstalledAsync(ct);
        if (!primary.IsOk)
            return primary;

        if (primary.Value)
            return Result<bool>.Ok(true);

        // CORROBORATING (best-effort, additive). An unreadable event log does not
        // demote the (false) primary to a failure — we simply have no extra signal.
        var corroborating = await _eventSignal.RecentSetupCompletionAsync(ct);
        if (corroborating.IsOk && corroborating.Value)
            return Result<bool>.Ok(true);

        return Result<bool>.Ok(false);
    }
}
