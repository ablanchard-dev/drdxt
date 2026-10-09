using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// The reconcilable DESCRIPTOR seam (plan §1 / §9).
//
// IReconcilabilityPolicy answers "which surface does this tweakId write?".
// IReconcilableTweakCatalog answers the NEXT question: "where on that surface does
// its current value live, and what canonical JSON does that value serialize to?" —
// the concrete read location (registry hive/path/name/kind, service name, or power
// plan identity) for each reconcilable tweakId.
//
// Single source of truth. This table is pure DATA (no OS dependency) so it lives in
// the pure DrDxt.Reconciler layer, not the OS-coupled adapters. Both consumers read
// from here:
//   * the SettingProbe (this bundle) — to know what to read and how to serialize it,
//   * the future op-mapper — to know which typed PrivilegedOp leaf to re-apply.
//
// It deliberately does not depend on DrDxt.Application: the Application catalog keys
// tweaks by a DIFFERENT id ("latency.reflex", "fixit.power_plan") and hides its
// concrete targets in private consts, so there is no clean id-keyed derivation, and
// referencing Application would pull NVAPI/ADLX/D3D11 into the verify-only host. The
// descriptors below mirror those Application consts exactly (the canonical reconciler
// id maps to the same hive/path/name the tweak writes), reviewed as an explicit table.
// -----------------------------------------------------------------------

/// <summary>A registry read location for a reconcilable tweak. <see cref="Kind"/>
/// is the value type written by the corresponding Application tweak; it drives both
/// the read and the canonical serialization.</summary>
public sealed record RegistryDescriptor(
    RegistryHiveKind Hive,
    string Path,
    string Name,
    RegistryValueType Kind);

/// <summary>The concrete read location for ONE reconcilable tweakId. Exactly one of
/// the surface-specific members is populated, matching <see cref="Surface"/>:
///   * Registry      → <see cref="Registry"/>,
///   * Service       → <see cref="ServiceName"/>,
///   * PowerPlan     → none (the active scheme GUID is read from the OS directly),
///   * ScheduledTask → none (no scheduled-task op leaf exists in V1).</summary>
public sealed record ReconcilableDescriptor(
    string TweakId,
    ReconcileSurface Surface,
    RegistryDescriptor? Registry = null,
    string? ServiceName = null);

/// <summary>The closed descriptor table: per reconcilable tweakId, its surface and
/// concrete read location. Mirrors the OS-write targets of the Application tweaks so
/// the probe reads exactly what the tweak wrote. Both the probe and the op-mapper
/// consume this — keeping the "where" in ONE place.</summary>
public interface IReconcilableTweakCatalog
{
    /// <summary>The descriptor for a reconcilable tweakId, or null when the id is not
    /// reconcilable / not in the closed table.</summary>
    ReconcilableDescriptor? DescriptorFor(string tweakId);
}

/// <summary>Canonical serialization for a reconcilable setting's CURRENT value, so a
/// probe read byte-matches the manifest's DesiredValueJson under ordinal equality
/// (the reconciler compares with StringComparison.Ordinal). The shape mirrors how the
/// Application tweaks serialize their values today: a DWORD/QWORD is its invariant
/// decimal string ("2"), a string value is itself, a power-plan/service value is the
/// stable token defined here. No JSON wrapping is added: the value IS the token, which
/// is exactly what the existing manifest carries (e.g. "1"/"0"/"2").</summary>
public static class ReconcilableValueCanonicalizer
{
    /// <summary>A DWORD/QWORD registry value → its invariant decimal string. Mirrors
    /// the Application tweaks' <c>value.ToString(CultureInfo.InvariantCulture)</c>.</summary>
    public static string Dword(uint value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A QWORD registry value → its invariant decimal string.</summary>
    public static string Qword(ulong value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A string registry value → itself (no extra quoting; the manifest
    /// carries the raw value).</summary>
    public static string String(string value) => value;

    /// <summary>The active power-plan GUID → its canonical 'D' form (lower-case,
    /// hyphenated, no braces) — the stable token the manifest stores.</summary>
    public static string PowerPlan(Guid activeScheme)
        => activeScheme.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>A service start/run state → a stable lower-case token
    /// ("notinstalled" / "stopped" / "running").</summary>
    public static string Service(ServiceRunState state) => state switch
    {
        ServiceRunState.NotInstalled => "notinstalled",
        ServiceRunState.Stopped => "stopped",
        ServiceRunState.Running => "running",
        _ => "unknown",
    };
}

/// <summary>The service run-state the probe reports for a Service-surface tweak. A
/// neutral mirror (no ServiceController coupling in the pure layer) so the canonical
/// token is defined here, next to the catalog the value belongs to.</summary>
public enum ServiceRunState
{
    NotInstalled = 0,
    Stopped = 1,
    Running = 2,
}

/// <summary>The production closed descriptor table. Keyed by the canonical reconciler
/// tweakId (the same ids <see cref="DefaultReconcilabilityPolicy"/> uses). Only ids
/// with a concrete, READABLE OS location are present — the policy may map an id to a
/// surface that has no plain readable leaf (e.g. an NVAPI/ADLX-backed "Registry" id),
/// and such ids are deliberately absent here so the probe reports CanProbe=false
/// rather than reading the wrong location.</summary>
public sealed class DefaultReconcilableTweakCatalog : IReconcilableTweakCatalog
{
    private static readonly IReadOnlyDictionary<string, ReconcilableDescriptor> Descriptors =
        new Dictionary<string, ReconcilableDescriptor>(StringComparer.Ordinal)
        {
            // NVIDIA driver "Low Latency Mode" — a real, readable DWORD leaf
            // (NvidiaUltraLowLatencyTweak: SOFTWARE\NVIDIA Corporation\Global\NVTweak\LowLatencyMode).
            ["latency.reflex.lowLatencyMode"] = new(
                "latency.reflex.lowLatencyMode",
                ReconcileSurface.Registry,
                Registry: new RegistryDescriptor(
                    RegistryHiveKind.LocalMachine,
                    @"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
                    "LowLatencyMode",
                    RegistryValueType.DWord)),

            // System active power plan — read via PowerGetActiveScheme (SanePowerPlanTweak).
            ["fixit.sanePowerPlan"] = new(
                "fixit.sanePowerPlan",
                ReconcileSurface.PowerPlan),

            // AMD 3D V-Cache Performance Optimizer service (X3dCcdParkingFixTweak).
            ["fixit.x3dVCacheService"] = new(
                "fixit.x3dVCacheService",
                ReconcileSurface.Service,
                ServiceName: "AMD3DVCacheSvc"),

            // NS-1 HAGS — HwSchMode = 2 (On) on the HKLM GraphicsDrivers leaf (HagsOnTweak).
            // HKLM is the one surface the LocalSystem boot reconciler reapplies correctly.
            ["os.hags"] = new(
                "os.hags",
                ReconcileSurface.Registry,
                Registry: new RegistryDescriptor(
                    RegistryHiveKind.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                    "HwSchMode",
                    RegistryValueType.DWord)),

            // NOTE — the NS-1 HKCU tweaks (Game DVR / Game Mode / VRR) are deliberately absent:
            // the boot reconciler is LocalSystem, where Registry.CurrentUser is SYSTEM's hive,
            // so an HKCU reapply would target the wrong hive. They persist natively across
            // reboot and the App re-applies them on next launch; reconciling them needs a
            // per-user reconcile path (see AppliedTweakManifestMapper / DefaultReconcilabilityPolicy).

            // NOTE — latency.renderQueueCap / latency.vrrVsyncCap restent OMIS, mais la raison
            // ecrite ici etait a moitie fausse et a ete corrigee le 21/08/2026.
            //
            // Ce qui reste vrai : ils n'ont pas de feuille de registre lisible, donc les sonder
            // comme une feuille lirait un emplacement bidon.
            // Ce qui etait faux : « le chemin de lecture DRS reste a resoudre ». Il existe et il
            // est reel — `NvApiLatencyReader` (Detection/Gpu/Nvapi) lit ces reglages via
            // `TryGetDrsGlobalSetting`, en NVAPI NON eleve : GetMaxPreRenderedFrames,
            // GetVsyncMode, GetFrameRateLimit, GetPreferredPstate.
            //
            // La vraie raison de l'omission, et elle est structurelle : le reconciliateur de boot
            // tourne en LocalSystem avec `DisabledNvApiActions` / `DisabledAdlxActions` — il est
            // VOLONTAIREMENT incapable d'ecrire le GPU (posture de securite). Les sonder
            // produirait donc une derive qu'il ne peut pas corriger. Le jour ou on les veut
            // reconciliables, la question n'est pas « comment les lire » (c'est fait) mais
            // « qui a le droit de les reecrire », et la reponse ne doit pas etre le service SYSTEM.
        };

    public ReconcilableDescriptor? DescriptorFor(string tweakId)
        => Descriptors.TryGetValue(tweakId, out var d) ? d : null;
}
