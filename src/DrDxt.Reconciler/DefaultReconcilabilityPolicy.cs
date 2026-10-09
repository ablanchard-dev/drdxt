namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// DefaultReconcilabilityPolicy — the production CLOSED table.
//
// It answers three questions for a tweakId: which OS surface it writes (or null if
// not reconcilable), whether it targets a PROTECTED surface (hard refuse), and
// which GPU vendor it is tied to (for the driver-drift signal). Everything is an
// explicit table — an unknown tweakId is conservatively not reconcilable (so a bogus
// id can never be silently re-applied).
//
// The PROTECTED blacklist (cahier §6) is the defence-in-depth guard the reconcile
// loop also enforces (T22): even if a future bug let one of these into the manifest,
// it is hard-refused. Defender / Windows Update / BITS are never touched.
//
// NOTE: this seeds the FOUNDATION's known surfaces. Wiring it to the full live
// tweak catalog (so every shipped tweak is mapped) lands with the host increment;
// new reconcilable tweaks are added here as an explicit, reviewed table entry.
// -----------------------------------------------------------------------
public sealed class DefaultReconcilabilityPolicy : IReconcilabilityPolicy
{
    // tweakId → OS surface. Firmware-category tweaks are deliberately absent (they
    // are not reconcilable; the store rejects them at save).
    private static readonly IReadOnlyDictionary<string, ReconcileSurface> Surfaces =
        new Dictionary<string, ReconcileSurface>(StringComparer.Ordinal)
        {
            // Latency / driver registry settings (NVCP / DRS-backed).
            ["latency.reflex.lowLatencyMode"] = ReconcileSurface.Registry,
            ["latency.renderQueueCap"] = ReconcileSurface.Registry,
            ["latency.vrrVsyncCap"] = ReconcileSurface.Registry,
            // System power plan (a feature update resets this to Balanced).
            ["fixit.sanePowerPlan"] = ReconcileSurface.PowerPlan,
            // AMD 3D V-Cache optimizer service.
            ["fixit.x3dVCacheService"] = ReconcileSurface.Service,
            // NS-1 HAGS — HKLM registry (a feature update can flip it back; LocalSystem can
            // reapply HKLM correctly). HKCU NS-1 tweaks (Game DVR / Game Mode / VRR) are not
            // listed: the boot reconciler is LocalSystem, where an HKCU write hits SYSTEM's
            // hive, so they need a per-user reconcile path (see AppliedTweakManifestMapper).
            ["os.hags"] = ReconcileSurface.Registry,
        };

    // tweakId → GPU vendor (for the per-vendor DriverVersion drift signal, I8).
    private static readonly IReadOnlyDictionary<string, string> Vendors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["latency.reflex.lowLatencyMode"] = "NVIDIA",
            ["latency.renderQueueCap"] = "NVIDIA",
        };

    // Protected surfaces (cahier §6) — never reconciled, hard-refused.
    private static readonly IReadOnlySet<string> Protected =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "service.WinDefend",
            "service.wuauserv",   // Windows Update
            "service.BITS",
            "service.WdNisSvc",   // Defender network inspection
            "service.SecurityHealthService",
        };

    public ReconcileSurface? SurfaceFor(string tweakId)
        => Surfaces.TryGetValue(tweakId, out var s) ? s : null;

    public bool IsProtected(string tweakId) => Protected.Contains(tweakId);

    public string? VendorFor(string tweakId)
        => Vendors.TryGetValue(tweakId, out var v) ? v : null;
}
