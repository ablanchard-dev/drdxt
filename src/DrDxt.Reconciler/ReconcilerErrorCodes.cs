namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// M4 reconciler error codes — module-local, namespaced so literals are never
// scattered (master plan §10 convention). Core ErrorCodes (IoFailure,
// ManifestTampered, OpNotAllowed…) are reused where they already fit.
// -----------------------------------------------------------------------
public static class ReconcilerErrorCodes
{
    // A manifest with an unknown / future schemaVersion → refuse, never guess (T5).
    public const string ManifestSchema = "MANIFEST_SCHEMA";

    // A committed manifest carried a tweak whose category is not OS-side
    // reconcilable (firmware-persistent: XMP/EXPO/undervolt BIOS) → refuse at
    // save (T7). The reconciler only owns volatile OS-side settings (cahier §4 P4).
    public const string ManifestNonReconcilable = "MANIFEST_NON_RECONCILABLE";

    // A tweakId reached the reconcile loop that is not mapped to an allowlisted
    // typed op (not in the verified manifest's known set) → refuse, security log (T20).
    public const string TweakNotAllowlisted = "TWEAK_NOT_ALLOWLISTED";

    // A tweakId targets a protected surface (Defender / Windows Update / BITS) →
    // hard refuse even if present in the manifest (T22, cahier §6 blacklist).
    public const string ProtectedSurface = "PROTECTED_SURFACE";

    // One or more items failed to reconcile; the others completed. Aggregated,
    // never a half-applied state (T19 / I2).
    public const string ReconcilePartial = "RECONCILE_PARTIAL";

    // A privileged write reported Ok but the confirming read-back did not show the
    // desired value (e.g. a GPO silently re-forces it) → item marked failed (T21).
    public const string ConfirmFailed = "RECONCILE_CONFIRM_FAILED";

    // A read probe failed during drift classification → fail-clean, no guess,
    // no write (T12).
    public const string ProbeFailed = "RECONCILE_PROBE_FAILED";

    // Clean uninstall (T37): the loop was stopped, the lease disarmed and the host
    // removed, but one or more "revert to original" writes failed (or did not confirm).
    // Aggregated and reported, never an orphaned still-reconciling install.
    public const string UninstallRevertPartial = "UNINSTALL_REVERT_PARTIAL";
}
