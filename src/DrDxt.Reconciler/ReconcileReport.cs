using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// The structured outcome of a reconcile pass / a drift detection pass.
//
// Returned ALONGSIDE the Result so the host can log/telemeter exactly what drifted,
// what was re-applied, what was notified, and what failed — without parsing strings.
// -----------------------------------------------------------------------

/// <summary>What the reconciler did with a single manifest item this pass.</summary>
public enum ItemOutcome
{
    /// <summary>Already at desired — no write (idempotence, I1).</summary>
    NoOp = 0,
    /// <summary>Drifted and successfully re-applied + confirmed.</summary>
    Reapplied = 1,
    /// <summary>Drifted but classified UserIntentional/Unknown → notified, not overwritten (I5).</summary>
    Notified = 2,
    /// <summary>Item is paused (kill-switch) → compared/logged but not re-applied (I7).</summary>
    Skipped = 3,
    /// <summary>Re-apply (or its confirming read-back) failed → item KO (I2).</summary>
    Failed = 4,
    /// <summary>Refused: not allowlisted / protected surface (I3, security).</summary>
    Refused = 5,
    /// <summary>Suspended by the anti boot-loop guard (T27b) — a GPU-tuning item was
    /// not re-applied because the machine is in a suspected boot-loop.</summary>
    Suspended = 6,
}

/// <summary>One line of the reconcile report.</summary>
public sealed record ReconcileItemResult(
    string TweakId,
    ItemOutcome Outcome,
    DriftCause? Cause,
    string? CurrentValueJson,
    Error? Error);

/// <summary>The whole pass. <see cref="GloballyPaused"/> is true when the global
/// kill-switch short-circuited the pass (I7 / T24).</summary>
public sealed record ReconcileReport(
    bool GloballyPaused,
    IReadOnlyList<ReconcileItemResult> Items)
{
    public IEnumerable<ReconcileItemResult> Reapplied => Items.Where(i => i.Outcome == ItemOutcome.Reapplied);
    public IEnumerable<ReconcileItemResult> Failed => Items.Where(i => i.Outcome == ItemOutcome.Failed);
    public IEnumerable<ReconcileItemResult> Notified => Items.Where(i => i.Outcome == ItemOutcome.Notified);
    public IEnumerable<ReconcileItemResult> Suspended => Items.Where(i => i.Outcome == ItemOutcome.Suspended);
}

/// <summary>A single item the detector found drifted (current != desired), with its
/// classified cause.</summary>
public sealed record DriftedItem(string TweakId, DriftCause Cause, string CurrentValueJson);
