namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// M3 — BIOS Doctor (foundation). Contracts for the detect→diff→Findings core.
//
// Strategy lock (M3-BIOS-doctor-decision.md): V1 = BIOS Doctor = read + guide +
// proof, never a BIOS write/script. The advisor here is pure read+diff: it takes
// the HardwareInventory + SettingsState already produced by M1 (it never
// re-detects them) and emits the firmware Findings that concern this machine.
//
// Honesty (plan §0.7, decision §9 / §22.3): a setting we cannot read = an honest
// "not read + why" Finding, never a fabricated state. "Nothing to tune in the
// BIOS" is a valid, assumed result.
//
// Guide steps / QR / mobile page / verify loop / IBiosAutomationProvider are
// LATER increments — deliberately not in these foundation contracts.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The firmware setting a <see cref="BiosFinding"/> concerns.
/// </summary>
public enum FirmwareSettingKind
{
    /// <summary>Memory overclock profile (XMP on Intel / EXPO on AMD / DOCP/A-XMP legacy).</summary>
    XmpExpo,

    /// <summary>Resizable BAR (NVIDIA) / Smart Access Memory (AMD).</summary>
    ReBar,

    /// <summary>Above 4G Decoding — a ReBAR prerequisite.</summary>
    Above4G,

    /// <summary>Compatibility Support Module — must be OFF (UEFI) for ReBAR; gated by boot mode.</summary>
    Csm,

    /// <summary>CPPC / Preferred Cores routing for AMD multi-CCD X3D (the V-Cache route).</summary>
    X3dCppc,

    /// <summary>PCIe link generation (Auto / Gen3 / Gen4 / Gen5) on the GPU slot.</summary>
    PcieGen,

    /// <summary>BIOS / AGESA firmware version (old firmware recommended for update).</summary>
    BiosVersion,
}

/// <summary>
/// Where a firmware setting sits in the decision-doc §2 action matrix.
/// V1 emits <see cref="Guide"/> for everything that needs changing (read + guide +
/// prove). <see cref="Read"/> is for informational, already-good or not-readable
/// states. <see cref="AutoOfficial"/> / <see cref="LabWhitelist"/> / <see cref="Forbidden"/>
/// describe the write pathway a future version MAY (or must never) use — they are
/// carried on the Finding for honesty/UX, but the foundation never writes.
/// </summary>
public enum BiosMatrixClass
{
    /// <summary>Read-only: state reported (good, or not readable). No change proposed.</summary>
    Read,

    /// <summary>Guide the user through the exact BIOS change (V1 default for fixes).</summary>
    Guide,

    /// <summary>Auto via a documented official vendor API/CLI on a supported machine (V2 only).</summary>
    AutoOfficial,

    /// <summary>Lab-whitelisted write: exact board + BIOS + setting + backup (V2/V3 only).</summary>
    LabWhitelist,

    /// <summary>A write path that is forbidden (raw UEFI write, WinRing0, BIOS/VBIOS flash, …).</summary>
    Forbidden,
}

/// <summary>
/// The recommended direction for a firmware setting. Foundation findings carry an
/// honest label key (<see cref="BiosFinding.RecommendedActionKey"/>) — not BIOS
/// menu navigation steps (those are a later guide increment).
/// </summary>
public enum BiosRecommendedAction
{
    /// <summary>Nothing to do — informational only (already good, or simply reported).</summary>
    None,

    /// <summary>Turn the setting ON (e.g. enable XMP/EXPO, enable ReBAR, enable Above-4G).</summary>
    Enable,

    /// <summary>Turn the setting OFF (e.g. disable CSM when booting in UEFI for ReBAR).</summary>
    Disable,

    /// <summary>Set the setting to its supported route (e.g. CPPC = Driver / Auto for X3D).</summary>
    SetSupportedRoute,

    /// <summary>Update the firmware (BIOS / AGESA) to a newer recommended version.</summary>
    Update,

    /// <summary>State could not be read — verify manually in the BIOS (honest unknown).</summary>
    Verify,
}

/// <summary>
/// An HONEST gain estimate for a mis-set firmware setting — a sourced RANGE with a
/// caveat, never a bare promised point-%. Mirrors M6's <c>GainEvidence</c> discipline
/// (decision §5: "on prouve, on ne promet pas"): a worst-case/typical range tied to a
/// cited source, plus a caveat saying when it is ≈0 (already well-tuned) or conditional
/// (e.g. only if mis-routed). Numbers are never fabricated — a setting with no sourced
/// figure carries a null range and leans on the caveat (e.g. game-dependent).
/// </summary>
/// <param name="MinPct">Low end of the honest range (e.g. ≈0 when already well-tuned); null = unquantified.</param>
/// <param name="MaxPct">High end of the honest range (worst-case/best-case upside); null = unquantified.</param>
/// <param name="Conditional">
/// True when the gain is realized only under a condition (e.g. the setting is actually
/// mis-set / mis-routed) and is likely ≈0 otherwise — drives conditional UI framing.
/// </param>
/// <param name="SourceKey">Resx key citing the in-repo source (the decision doc). Never blank.</param>
/// <param name="CaveatKey">Resx key for the honesty caveat ("≈0 if already correct" / game-dependent…). Never blank.</param>
public record BiosGainEstimate(
    double? MinPct,
    double? MaxPct,
    bool Conditional,
    string SourceKey,
    string CaveatKey);

/// <summary>
/// A firmware-level Finding that concerns this machine, produced by detect→diff
/// over M1's <see cref="HardwareInventory"/> + <see cref="SettingsState"/>.
/// Pure constat + rationale + recommended direction — no menu steps, no write.
/// </summary>
/// <param name="Kind">The firmware setting concerned.</param>
/// <param name="Severity">How impactful it is when mis-set (Info / Minor / Major).</param>
/// <param name="TitleKey">Resx key for the finding title (FR+EN). Never hard-coded text.</param>
/// <param name="RationaleKey">Resx key for the "why" (honesty). Never hard-coded text.</param>
/// <param name="MatrixClass">Decision-doc §2 class of the setting's write pathway.</param>
/// <param name="CurrentState">Read state if available; <see cref="TriState.Unknown"/> when not readable.</param>
/// <param name="RecommendedAction">The honest recommended direction (no menu steps here).</param>
/// <param name="RecommendedActionKey">Resx key for the recommended-action label (FR+EN).</param>
/// <param name="Gain">
/// An honest sourced gain RANGE with a caveat when this is mis-set (never a bare promised
/// point-%); null when not quantified. See <see cref="BiosGainEstimate"/>.
/// </param>
/// <param name="StateNotReadable">
/// True when <see cref="CurrentState"/> could not be read (honest "not read + why").
/// The Finding then states the unknown and never asserts a false state.
/// </param>
public record BiosFinding(
    FirmwareSettingKind Kind,
    Severity Severity,
    string TitleKey,
    string RationaleKey,
    BiosMatrixClass MatrixClass,
    TriState CurrentState,
    BiosRecommendedAction RecommendedAction,
    string RecommendedActionKey,
    BiosGainEstimate? Gain,
    bool StateNotReadable);

/// <summary>
/// Detect→diff over M1's already-produced inventory + settings, emitting the
/// firmware Findings that concern this machine. Never re-detects, never writes.
/// </summary>
public interface IBiosAdvisor
{
    /// <summary>
    /// Produces the firmware Findings applicable to <paramref name="hw"/> given
    /// <paramref name="state"/>. A well-tuned machine yields an empty list (valid,
    /// assumed result). Incoherent input fails cleanly via <see cref="Result"/>.
    /// </summary>
    Result<IReadOnlyList<BiosFinding>> Advise(HardwareInventory hw, SettingsState state);
}
