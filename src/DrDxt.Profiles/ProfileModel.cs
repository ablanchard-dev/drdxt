using DrDxt.Core;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// M6 — Per-chip optimization profiles (cahier §3 transversal "Base de profils",
// §10 "Base de profils = le vrai moat", §5 "fix-misconfig first", decision
// interne undervolt NVIDIA option A contre B).
//
// A PROFILE is a named, chip-targeted set of tweak SELECTIONS (+ parameters). It does
// Not apply anything and holds no privileged primitive — it is the curated, AUDITABLE
// recommendation layer that sits in front of the M2 catalog/engine. Each selection
// references an existing M2 catalog tweak id and carries its own honest provenance:
// every claimed effect cites a measurement or a sourced range, plus a confidence and a
// safety level. There are NO invented numbers here (cahier §11 garde-fou anti-Outbyte:
// "ne jamais inventer de problème"; §5 "on prouve, on ne promet pas" — the *real* delta
// is still proven by the engine's A/B re-bench, never by this table).
//
// Pure data: no I/O, no DI, fully unit-testable. The values are conservative and stay
// subject to GpuSafeBounds re-clamping/gating downstream — a profile is a REQUEST, not
// an authorization (mirrors GpuTuningTarget's own contract).
// -----------------------------------------------------------------------

/// <summary>How strongly a claimed effect is grounded — honesty ladder (cahier §5/§11).
/// <list type="bullet">
/// <item><b>Measured</b> — observed in an A/B run on real hardware (note the sample).</item>
/// <item><b>Sourced</b> — a published benchmark / research range (cited in Source).</item>
/// <item><b>Inferred</b> — a conservative heuristic, not a number (used sparingly).</item>
/// </list></summary>
public enum GainConfidence { Measured, Sourced, Inferred }

/// <summary>How safe a selection is to apply (cahier §8: aggressive tuning = Expert/Lab).
/// The foundation profiles are all <see cref="Safe"/> — in-bounds, supported routes,
/// fully reversible.</summary>
public enum SafetyLevel { Safe, Moderate, Expert }

/// <summary>What a selection is FOR. Drives the "fix-misconfig first" ordering
/// (cahier §5: repair before tuning) and the UI grouping. The enum order IS the apply
/// priority: FixMisconfig → Latency → GpuTune.</summary>
public enum ProfileIntent { FixMisconfig, Latency, GpuTune }

/// <summary>One named parameter for a tweak (e.g. <c>CoreOffsetMhz = 75</c>). Maps onto
/// the M2 tuning targets at the consumption seam (e.g. <c>GpuTuningTarget.CoreOffsetMhz</c>,
/// <c>GpuPowerLimitTarget.PowerLimitPct</c>). Integer scalars only in the foundation;
/// still re-clamped by GpuSafeBounds before any write.</summary>
public sealed record ProfileParameter(string Key, int Value, string Unit);

/// <summary>The honest provenance of ONE claimed effect: a conservative, non-promising
/// description of the effect, the exact in-repo source it comes from, and its confidence
/// + safety. <see cref="Claim"/> must never read as a guaranteed FPS promise (§6) and
/// <see cref="Source"/> must never be blank (enforced by tests).</summary>
public sealed record GainEvidence(
    string Claim,
    string Source,
    GainConfidence Confidence,
    SafetyLevel Safety);

/// <summary>A profile's selection of ONE M2 catalog tweak: the tweak id, what it is for,
/// its parameters, and the provenance of its claimed gain.</summary>
public sealed record ProfileTweakSelection(
    string TweakId,
    ProfileIntent Intent,
    IReadOnlyList<ProfileParameter> Parameters,
    GainEvidence Evidence);

/// <summary>The coarse chip family a profile targets. Conservative on purpose — an
/// <see cref="GenericSafe"/> baseline is always available; per-chip families are added
/// only when detection is confident (cahier §10: detection universal, profiles by waves).</summary>
public enum ChipFamily { GenericSafe, AmdX3dDualCcd, NvidiaOptionA }

/// <summary>A named, chip-targeted set of tweak selections. The product's moat layer
/// (cahier §10). <see cref="Tested"/> reflects whether the team bench has VALIDATED this
/// profile on real hardware (cahier §10 "profils testés déployés par vagues") — false
/// foundation profiles are still recommendable but the UI labels them "non vérifié"
/// (cahier §16).</summary>
public sealed record ChipProfile(
    string Id,
    string NameKey,
    ChipFamily Family,
    IReadOnlyList<ProfileTweakSelection> Selections,
    bool Tested,
    string Summary)
{
    /// <summary>The catalog tweak ids this profile references, in fix-misconfig-first
    /// order (cahier §5). Used to compose a TweakPlan and audited against the real M2
    /// catalog by tests.</summary>
    public IReadOnlyList<string> OrderedTweakIds =>
        Selections
            .OrderBy(s => (int)s.Intent)
            .Select(s => s.TweakId)
            .ToList();
}
