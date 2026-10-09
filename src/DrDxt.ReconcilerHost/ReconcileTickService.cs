using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// ReconcileTickService (plan T26) — one reconcile pass, fail-clean on a bad manifest.
//
// A "tick" is: Check the license entitlement, load the SIGNED desired-state manifest,
// then reconcile. The store's LoadAsync VERIFIES the signature before returning, so a
// tamper / schema / IO error surfaces here as a Fail. The security-critical rule (I4): a
// failed load never reaches the reconciler — we do not enter a write loop on a manifest we
// cannot trust. Instead we journal a SECURITY diagnostic and return the Fail verbatim.
//
// FINDING 1 — ENTITLEMENT GATE (the moat blocker fix): Before we would load/reconcile, the
// IEntitlementGate confirms the signed manifest still carries a live, bounded-validity
// license entitlement (DesiredStateManifest.ExpiresAt). The LocalSystem reconciler runs
// offline and cannot read the user's license cache, so this is the moat's fail-closed
// license signal: when the license lapses/is revoked the App stops refreshing the manifest
// → the entitlement expires within EntitlementTtl → we refuse to reapply (the paid moat
// stops). We do not auto-revert already-applied tweaks (owner's call). Any ambiguity
// (missing/expired entitlement, unverifiable manifest) fails CLOSED.
//
// All the privileged work lives in the injected IReconciler; this class only sequences
// entitlement → load → reconcile and enforces the fail-clean gates, so it is fully
// unit-testable with a fake gate, a fake store (Ok / Fail) and a substitute reconciler.
// -----------------------------------------------------------------------
public sealed class ReconcileTickService
{
    private readonly IDesiredStateStore _store;
    private readonly IReconciler _reconciler;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly IEntitlementGate _entitlement;

    public ReconcileTickService(
        IDesiredStateStore store,
        IReconciler reconciler,
        IDiagnosticsLog diagnostics,
        IEntitlementGate entitlement)
    {
        _store = store;
        _reconciler = reconciler;
        _diagnostics = diagnostics;
        _entitlement = entitlement;
    }

    /// <summary>FINDING 1 + I4 — checks the live license entitlement, then loads the verified
    /// manifest and reconciles it. An absent/expired entitlement OR a load failure
    /// (tamper/schema/IO) is logged as a security event and returned without reconciling —
    /// never a write loop on an unentitled or untrusted manifest (fail-closed).</summary>
    public async Task<Result> RunTickAsync(CancellationToken ct)
    {
        // FINDING 1 — entitlement gate first (fail-closed). If the manifest's bounded-validity
        // entitlement is absent/expired (license lapsed/revoked → App stopped refreshing) or
        // the manifest is unverifiable, refuse to reapply: journal a SECURITY diagnostic and
        // return the Fail without touching the reconciler. The already-applied tweaks are left
        // as-is (no auto-revert — owner's call).
        var entitled = await _entitlement.EnsureReapplyEntitledAsync(ct);
        if (!entitled.IsOk)
        {
            var entErr = entitled.Error!.Value;
            _diagnostics.Change(
                tweakId: HostActors.ManifestTweakId,
                from: entErr.Code,
                to: "reconcile-skipped-entitlement",
                actor: HostActors.Security);
            return entitled;
        }

        var loaded = await _store.LoadAsync();
        if (!loaded.IsOk)
        {
            var error = loaded.Error!.Value;
            // SECURITY log: the actor flags this as a security event for the audit
            // journal. We deliberately do not call the reconciler (fail-clean, I4).
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
