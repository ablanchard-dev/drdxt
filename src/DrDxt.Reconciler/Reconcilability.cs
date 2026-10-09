using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Which settings the reconciler is allowed to own.
//
// DURABILITY scope (cahier §4 P4): only VOLATILE OS-side surfaces are reconciled
// (registry non-policy, services, power plan, scheduled tasks). FIRMWARE-persistent
// settings (XMP/EXPO, BIOS undervolt) live in the BIOS (M3) and are OUT of scope —
// the manifest must never carry them (plan T7).
//
// PROTECTED surfaces (cahier §6 blacklist) can never be touched, even if a tweak
// somehow ends up in the manifest pointing at them (plan T22 hard guard).
// -----------------------------------------------------------------------

/// <summary>The OS-side surface a reconciled tweak writes to. Mirrors the four
/// write ops the ElevatedHelper exposes (registry / service / power plan /
/// scheduled task). <see cref="Firmware"/> is included only so the policy can
/// REJECT it — the reconciler never writes firmware.</summary>
public enum ReconcileSurface
{
    Registry = 0,
    Service = 1,
    PowerPlan = 2,
    ScheduledTask = 3,

    /// <summary>Firmware-persistent (BIOS). Not reconcilable — rejected at save.</summary>
    Firmware = 99,
}

/// <summary>Decides whether a tweak may be persisted/reconciled and which OS surface
/// it maps to. The default policy is a CLOSED table keyed by tweakId; an unknown
/// tweakId is treated conservatively (not reconcilable) so a bogus id can never be
/// silently re-applied.</summary>
public interface IReconcilabilityPolicy
{
    /// <summary>The surface a known reconcilable tweak writes to, or null if the
    /// tweak is unknown / not reconcilable.</summary>
    ReconcileSurface? SurfaceFor(string tweakId);

    /// <summary>True when the tweak targets a protected surface (Defender / Windows
    /// Update / BITS …) and must never be touched.</summary>
    bool IsProtected(string tweakId);

    /// <summary>The GPU vendor this tweak is tied to ("NVIDIA" / "AMD"), or null for
    /// a non-GPU tweak. Lets drift classification treat a per-vendor DriverVersion
    /// change as a (non-user) WindowsReset-class cause for that tweak (I8, T8b/T8c).
    /// A default of null keeps non-GPU policies simple.</summary>
    string? VendorFor(string tweakId) => null;

    /// <summary>Convenience: a tweak is reconcilable iff it has a known non-firmware
    /// surface AND is not protected.</summary>
    bool IsReconcilable(string tweakId)
        => !IsProtected(tweakId)
           && SurfaceFor(tweakId) is { } s
           && s != ReconcileSurface.Firmware;
}
