namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// M3 — BIOS Doctor: the GUIDE GENERATOR (content). Not the rendering.
//
// This round = the *content* of the personalized BIOS guide only:
//   • the guide MODEL (records below),
//   • the guide LIBRARY (IGuideLibrary): per vendor × BIOS-UI generation, the
//     Exact menu paths per setting (data-driven JSON, validated defensively),
//   • the GENERATOR (IGuideGenerator): from the actionable BiosFindings + the
//     board/BIOS identity → the PersonalizedGuide.
//
// PRECISION = the product (decision §9). A covered board → exact path + Verified.
// An uncovered board → a labelled "unverified for your exact board" fallback that
// Always carries the exact setting NAME to look for (the client never needs a
// third-party tool) + a Clear-CMOS recovery net.
//
// SAFETY (§16):
//   • Legacy/MBR boot → never a "CSM off" step (no-boot guard).
//   • ReBAR ordered prerequisites → Above 4G → CSM OFF → Re-Size BAR, in order.
//   • Recovery section is MANDATORY, at the TOP and the BOTTOM of every guide.
//
// Never a BIOS write/script: a guide is human-readable text/steps only.
//
// Rendering (HTML/mobile page), QR, PDF, signed URL/TTL, post-reboot verify loop,
// IBiosAutomationProvider — deliberately not in this increment.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The motherboard BIOS vendor a guide is keyed on. Derived from
/// <see cref="MotherboardInfo.Manufacturer"/> (case/space-insensitive mapping).
/// </summary>
public enum BiosVendor
{
    /// <summary>Vendor not recognized → the most prudent generic fallback is used.</summary>
    Unknown,

    /// <summary>ASUS / ASUSTeK.</summary>
    Asus,

    /// <summary>MSI / Micro-Star.</summary>
    Msi,

    /// <summary>Gigabyte / Aorus.</summary>
    Gigabyte,

    /// <summary>ASRock.</summary>
    Asrock,
}

/// <summary>
/// The system boot mode, derived from <see cref="BiosInfo.IsUefi"/>. Gates the
/// CSM-off advice: CSM may only be disabled when booting in <see cref="Uefi"/>
/// (disabling it on a Legacy/MBR install risks a no-boot — §16).
/// </summary>
public enum BootMode
{
    /// <summary>UEFI boot — CSM-off advice is safe to give.</summary>
    Uefi,

    /// <summary>Legacy/CSM boot — CSM-off advice is SUPPRESSED (no-boot risk).</summary>
    Legacy,

    /// <summary>Boot mode could not be determined — treated as unsafe for CSM-off.</summary>
    Unknown,
}

/// <summary>
/// How trustworthy the menu paths in a <see cref="PersonalizedGuide"/> are.
/// Honesty: an unverified guide is clearly marked so the client knows the paths
/// are a best-known fallback, not confirmed for their exact board.
/// </summary>
public enum GuideConfidence
{
    /// <summary>
    /// The exact board + BIOS version is in the curated, bench-verified KB (decision
    /// §9.1: the KB key is board + BIOS version, not the brand) → the menu paths are
    /// Exact for this machine. A vendor / UI-generation match is never Verified — it is
    /// only the labelled <see cref="UnverifiedFallback"/>.
    /// </summary>
    Verified,

    /// <summary>
    /// The exact board is not (yet) covered → the guide uses the best-known
    /// per-vendor (or most-prudent generic) fallback, labelled "unverified for your
    /// exact board". It still carries the exact setting NAME to look for + Clear-CMOS.
    /// </summary>
    UnverifiedFallback,
}

/// <summary>
/// One ordered, human-readable step of a personalized BIOS guide. It is text for a
/// human — never a write/script.
/// </summary>
/// <param name="Order">1-based position within its section (steps are emitted in order).</param>
/// <param name="InstructionKey">Resx key (FR+EN) for the step instruction. Never hard-coded text.</param>
/// <param name="ExactValue">
/// The exact navigation value for this step when known — e.g. the menu path
/// ("Ai Tweaker &gt; Ai Overclock Tuner"), the toggle name ("EXPO I"), or a key
/// ("F10"). For a fallback step this is the exact setting NAME to search for (so the
/// client never needs a third party). Null only for pure narrative/recovery steps.
/// </param>
/// <param name="Setting">The firmware setting this step concerns (for grouping/ordering).</param>
/// <param name="IsRecoveryRelated">True for safety/recovery steps (rendered with care).</param>
public record GuideStep(
    int Order,
    string InstructionKey,
    string? ExactValue,
    FirmwareSettingKind Setting,
    bool IsRecoveryRelated);

/// <summary>
/// The mandatory safety/recovery section. It is shown at the TOP (so the client is
/// reassured before touching the BIOS) AND at the BOTTOM (so it is there when a
/// reboot goes sideways). §16: includes the no-boot reassurance, the memory-training
/// wait, Clear-CMOS, Load Optimized Defaults, and the "one change at a time" ladder.
/// </summary>
/// <param name="Top">Recovery steps placed at the top of the guide (non-empty).</param>
/// <param name="Bottom">Recovery steps placed at the bottom of the guide (non-empty).</param>
/// <param name="CsmAdviceSuppressed">
/// True when a CSM-off step was suppressed because the system does not boot UEFI
/// (a warning step replaces it). Drives the honest "we did not advise CSM-off" note.
/// </param>
public record RecoverySection(
    IReadOnlyList<GuideStep> Top,
    IReadOnlyList<GuideStep> Bottom,
    bool CsmAdviceSuppressed);

/// <summary>
/// The fully assembled, machine-targeted BIOS guide content (NO rendering). It shows
/// Only what is mis-set on this machine, with exact menu paths (or a labelled
/// fallback), the ReBAR dependency order, and the mandatory recovery section top+bottom.
/// </summary>
/// <param name="Vendor">The resolved BIOS vendor (for the board label / fallback).</param>
/// <param name="BiosUiGeneration">The resolved BIOS-UI generation token (e.g. "Click BIOS 5").</param>
/// <param name="DetectedBoardLabel">Human label of the detected board (Manufacturer + Product).</param>
/// <param name="Confidence">Verified (covered board) vs UnverifiedFallback (best-known).</param>
/// <param name="BootMode">The resolved boot mode (gates CSM-off).</param>
/// <param name="Findings">The actionable findings this guide addresses (Guide-class only).</param>
/// <param name="Steps">The ordered per-finding instruction steps (recovery lives in <see cref="Recovery"/>).</param>
/// <param name="Recovery">The mandatory top+bottom recovery section.</param>
/// <param name="EnterKey">The key to enter the BIOS (e.g. "Del", "F2"), carried verbatim from the resolved template. May be blank when the template does not specify one.</param>
/// <param name="SaveKey">The key to save &amp; exit the BIOS (e.g. "F10"), carried verbatim from the resolved template. May be blank when the template does not specify one.</param>
/// <param name="BoardProduct">07/10/2026 (D12) : le modèle seul, tel que Windows le lit (Win32_BaseBoard.Product), pour
/// pointer le manuel du constructeur.</param>
public record PersonalizedGuide(
    BiosVendor Vendor,
    string BiosUiGeneration,
    string DetectedBoardLabel,
    GuideConfidence Confidence,
    BootMode BootMode,
    IReadOnlyList<BiosFinding> Findings,
    IReadOnlyList<GuideStep> Steps,
    RecoverySection Recovery,
    string EnterKey,
    string SaveKey,
    string BoardProduct = "");

// ── Library data model (deserialized from embedded JSON, validated defensively). ──

/// <summary>
/// The per-setting menu navigation for one vendor × BIOS-UI generation, used to
/// build <see cref="GuideStep.ExactValue"/>. Every setting carries both the exact
/// menu path AND the exact setting name to search for (the fallback can always give
/// the name even when the path is generic).
/// </summary>
/// <param name="Setting">The firmware setting this entry navigates to.</param>
/// <param name="MenuPath">The exact BIOS menu path (e.g. "Ai Tweaker &gt; Ai Overclock Tuner"). May be empty for a name-only fallback.</param>
/// <param name="SettingName">The exact on-screen setting name to look for (e.g. "EXPO", "Re-Size BAR Support"). Never empty.</param>
/// <param name="TargetValue">The exact value to set (e.g. "EXPO I", "Enabled", "Disabled").</param>
public record GuideSettingPath(
    FirmwareSettingKind Setting,
    string MenuPath,
    string SettingName,
    string TargetValue);

/// <summary>
/// A curated guide template. Most templates are per-vendor (× BIOS-UI generation)
/// best-known FALLBACKS. A genuine KB entry additionally carries <see cref="Board"/> +
/// <see cref="BiosVersion"/> — the only key (decision §9.1) that can be
/// <see cref="IsVerified"/>. The validator REJECTS any <see cref="IsVerified"/> template
/// that lacks that board + BIOS-version key, so a vendor/UI-generation entry can never
/// claim to be "verified for your exact board". Loaded from embedded JSON, validated
/// before use.
/// </summary>
/// <param name="Vendor">The vendor this template targets.</param>
/// <param name="BiosUiGeneration">The BIOS-UI generation token (e.g. "Click BIOS 5"), or "Generic".</param>
/// <param name="IsVerified">
/// True only for a bench-verified board + BIOS-version KB entry; false for every
/// per-vendor / UI-generation / generic fallback. Enforced by the validator: a true
/// value without a <see cref="Board"/> + <see cref="BiosVersion"/> is rejected as corrupt.
/// </param>
/// <param name="Board">
/// The exact board this entry was bench-verified on (e.g. "ROG STRIX B650-A GAMING WIFI"),
/// matched against <see cref="MotherboardInfo.Product"/>. Empty for a vendor/generic fallback.
/// </param>
/// <param name="BiosVersion">
/// The exact BIOS version this entry was verified against (matched against
/// <see cref="BiosInfo.Version"/>). Empty for a vendor/generic fallback. Together with
/// <see cref="Board"/> it forms the board + BIOS-version KB key (decision §9.1).
/// </param>
/// <param name="EnterKey">The key to enter the BIOS (e.g. "Del", "F2").</param>
/// <param name="SaveKey">The key to save &amp; exit (e.g. "F10").</param>
/// <param name="Paths">The per-setting navigation entries.</param>
public record GuideTemplate(
    BiosVendor Vendor,
    string BiosUiGeneration,
    bool IsVerified,
    string Board,
    string BiosVersion,
    string EnterKey,
    string SaveKey,
    IReadOnlyList<GuideSettingPath> Paths);

/// <summary>
/// Resolves the curated <see cref="GuideTemplate"/> for a machine. Always succeeds with
/// a usable template. Resolution precedence (decision §9.1):
/// <list type="number">
///   <item>a bench-verified <b>board + BIOS-version</b> KB entry — the only match that
///   can be <see cref="GuideTemplate.IsVerified"/> ("verified for your exact board");</item>
///   <item>the per-vendor × BIOS-UI generation best-known fallback (never verified);</item>
///   <item>the per-vendor generic fallback;</item>
///   <item>the cross-vendor generic fallback.</item>
/// </list>
/// A labelled fallback (<see cref="GuideTemplate.IsVerified"/> = false) still carries the
/// exact setting names, so the client is never stranded. A hard failure is reserved for
/// internally corrupt library data (defensive validation), never for an uncovered board.
/// </summary>
public interface IGuideLibrary
{
    /// <summary>
    /// Returns the best template for the machine. When <paramref name="board"/> +
    /// <paramref name="biosVersion"/> match a verified KB entry, that exact (Verified)
    /// entry is returned; otherwise the best-known per-vendor / generic fallback (never
    /// verified). Fails only on corrupt library data.
    /// </summary>
    /// <param name="vendor">The resolved BIOS vendor.</param>
    /// <param name="biosUiGeneration">The BIOS-UI generation token (fallback key).</param>
    /// <param name="board">The detected board product (the KB key, with <paramref name="biosVersion"/>); optional.</param>
    /// <param name="biosVersion">The detected BIOS version (the KB key, with <paramref name="board"/>); optional.</param>
    Result<GuideTemplate> Resolve(
        BiosVendor vendor, string biosUiGeneration, string? board = null, string? biosVersion = null);
}

/// <summary>
/// Assembles the <see cref="PersonalizedGuide"/> from the actionable
/// (<see cref="BiosMatrixClass.Guide"/>) findings + the board/BIOS identity. Shows
/// Only what is mis-set, with exact paths (or labelled fallback), the ReBAR
/// dependency order, the mandatory recovery section top+bottom, and the §16 guards
/// (Legacy → no CSM-off; uncovered board → Unverified; ReBAR NVIDIA Unknown findings
/// are non-Guide and yield no write step). Never produces a write.
/// </summary>
public interface IGuideGenerator
{
    /// <summary>
    /// Builds the personalized guide for <paramref name="hw"/> given the
    /// <paramref name="findings"/> (only Guide-class findings yield write steps).
    /// Fails cleanly via <see cref="Result"/> on corrupt library data.
    /// </summary>
    Result<PersonalizedGuide> Generate(HardwareInventory hw, IReadOnlyList<BiosFinding> findings);
}
