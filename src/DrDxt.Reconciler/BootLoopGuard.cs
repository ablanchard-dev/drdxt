using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// BootLoopGuard (plan T27b) — the anti boot-loop safety.
//
// A persisted-but-now-bad GPU-tuning item (a clock/power offset that crashes the
// driver at boot) could trap the machine in a loop if the reconciler re-imposes it
// on every boot. The volatility of NVAPI (a reboot returns to stock) no longer
// protects you once the reconciler re-applies it each boot.
//
// So: if the consecutive unclean-boot streak is at/over a threshold, the guard
// SUSPENDS re-applying GPU-tuning items (vendor-tagged in the policy) and flags it.
// Safe OS-side items (registry / power-plan / service / task) keep reconciling —
// only the GPU-tuning class is suspended.
//
// Pure decision logic over an injected IBootOutcomeStore (fake streak/clock in
// tests). The host owns marking boot started/stable — that wiring is the NEXT
// increment; here the guard only READS the streak and decides.
// -----------------------------------------------------------------------
public sealed class BootLoopGuard
{
    private readonly IBootOutcomeStore _bootStore;
    private readonly int _threshold;

    /// <param name="threshold">Consecutive unclean boots at which GPU-tuning re-apply
    /// is suspended (default 3 per plan T27b).</param>
    public BootLoopGuard(IBootOutcomeStore bootStore, int threshold = 3)
    {
        _bootStore = bootStore;
        _threshold = threshold;
    }

    /// <summary>True iff GPU-tuning re-apply must be suspended right now (the unclean
    /// boot streak is at/over the threshold). A read failure is treated CONSERVATIVELY
    /// as "suspend" — if we cannot prove boots are healthy, we do not re-impose a GPU
    /// tweak that might be the cause of the loop (fail-safe, not fail-open).</summary>
    public async Task<bool> ShouldSuspendGpuReapplyAsync(CancellationToken ct)
    {
        var streak = await _bootStore.GetUncleanStreakAsync(ct);
        if (!streak.IsOk)
            return true; // conservative: cannot confirm healthy boots → suspend GPU re-apply.
        return streak.Value >= _threshold;
    }
}
