namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// Stable, non-localized tokens the host writes into the IDiagnosticsLog change
// journal. The journal is a machine-parseable audit trail (not UI), so these are
// fixed identifiers — never user-facing strings. UI text the host shows a human
// goes through ILocalizer (resx keys), kept separate from these.
// -----------------------------------------------------------------------
internal static class HostActors
{
    /// <summary>Journal actor for a security event (tampered/unverifiable manifest,
    /// refused reconcile). Lets a log scan filter security entries deterministically.</summary>
    public const string Security = "DrDXT/security";

    /// <summary>The journal "tweak" slot used for manifest-level (not per-tweak)
    /// security events — the bad-manifest gate is about the whole manifest.</summary>
    public const string ManifestTweakId = "manifest";
}
