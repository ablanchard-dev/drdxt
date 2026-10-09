namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// M3 — BIOS Doctor: the CLOSED LOOP (post-reboot verification). Logic only.
//
// The loop has two halves, both pure OS-marker + re-read (never a BIOS write):
//
//   1. Before the user reboots into the BIOS, we persist a PendingBiosChange marker
//      (which firmware setting(s) they are about to change, the state we expect each
//      to end in, when, and a light machine id). IPendingBiosChangeStore owns that
//      marker — atomic write, fail-clean read (absent = nothing pending, corrupt =
//      an honest Result.Fail, never a throw, never a fabricated "pending").
//
//   2. At the next Windows return, IBiosLoopVerifier takes that marker + the FRESH
//      SettingsState re-read by M1 and compares, per setting: Took / DidNotTake /
//      Unreadable. Honesty (decision §9): a setting that did not take — or that we
//      cannot read — is SAID so, with a recovery card (retry one change at a time,
//      Clear-CMOS, Load Optimized Defaults). It is never reported as a false success.
//      If everything took, the marker is cleaned up. No marker → an idempotent no-op.
//
// This file is contracts only (records + interfaces + the injectable clock seam).
// Rendering (HTML/QR/PDF/signed URL), live inspectors and IBiosAutomationProvider
// are deliberately not here.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Injectable UTC clock for the BIOS closed loop — the marker timestamp is read only
/// through this seam so a FakeClock yields a fixed, asserted value. DateTime.Now /
/// DateTimeOffset.UtcNow are forbidden in this path. A module-local one-liner matches
/// the rest of the codebase (the reconciler, the elevated helper and licensing each
/// keep their own rather than coupling projects).
/// </summary>
public interface IBiosClock
{
    /// <summary>The current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock — the real UTC wall-clock.</summary>
public sealed class SystemBiosClock : IBiosClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Injectable, light, non-secret machine identity for the BIOS closed loop. The verifier
/// reads the CURRENT machine id only through this seam and compares it to the pending
/// marker's <see cref="PendingBiosChange.MachineId"/>: a marker copied from another PC
/// must not be honored on this one. A module-local one-liner matches the rest of the
/// codebase (see <see cref="IBiosClock"/>).
/// </summary>
public interface IBiosMachineId
{
    /// <summary>The current machine's light, non-secret identity.</summary>
    string Current { get; }
}

/// <summary>Production machine identity — the OS machine name (light, non-secret).</summary>
public sealed class SystemBiosMachineId : IBiosMachineId
{
    /// <inheritdoc />
    public string Current => Environment.MachineName;
}

/// <summary>
/// One firmware setting the user is about to change, with the state we expect it to
/// hold when we re-read it after the reboot. The closed loop compares the FRESH
/// <see cref="SettingsState"/> against this expectation per setting.
/// </summary>
/// <param name="Setting">The firmware setting being changed.</param>
/// <param name="ExpectedState">
/// The state we expect after the change (e.g. <see cref="TriState.On"/> for "enable
/// XMP/ReBAR"). <see cref="TriState.Unknown"/> is not a valid expectation — a pending
/// change must name a concrete target — and is rejected by the store as corrupt.
/// </param>
/// <param name="ObservedBefore">
/// The MEASURED quantity for this setting at the instant the marker was written (for
/// <see cref="FirmwareSettingKind.XmpExpo"/>: the configured memory clock in MHz).
/// <c>null</c> = no scalar was captured, and the loop falls back to the TriState compare.
///
/// Why it exists: the loop's question is not "is XMP on" but "what CHANGED across this
/// reboot". 2133 -> 3800 is a fact; probably-On -> probably-Off is a bet on a bet, and on a
/// kit whose JEDEC floor is exceeded in both states the inference does not move at all while
/// the machine does. Optional/trailing so existing 2-arg constructions stay source-compatible.
/// </param>
public record PendingSettingTarget(
    FirmwareSettingKind Setting,
    TriState ExpectedState,
    long? ObservedBefore = null);

/// <summary>
/// The on-disk "a firmware change is pending a reboot" marker, written before the user
/// goes into the BIOS and read once Windows comes back. Persisted atomically; read
/// fail-clean (absent = nothing pending; corrupt = an honest failure, never trusted).
/// </summary>
/// <param name="Targets">
/// The one-or-more settings being changed with their expected post-change state. An
/// empty list is not a real pending change and is rejected as corrupt.
/// </param>
/// <param name="CreatedUtc">When the marker was written (from <see cref="IBiosClock"/>, never DateTime.Now).</param>
/// <param name="MachineId">
/// A light, non-secret machine identity (so a marker copied to another PC is not blindly
/// applied to it). The verifier compares it against <see cref="IBiosMachineId.Current"/>;
/// on a mismatch the foreign marker is never honored — it is treated as nothing-pending
/// and left untouched (not cleared, not evaluated, never reported as a success/failure
/// for this machine).
/// </param>
public record PendingBiosChange(
    IReadOnlyList<PendingSettingTarget> Targets,
    DateTimeOffset CreatedUtc,
    string MachineId);

/// <summary>
/// Per-setting verdict of the closed loop — honest by construction.
/// </summary>
public enum BiosSettingVerdict
{
    /// <summary>The fresh re-read matches the expected state — the change took.</summary>
    Took,

    /// <summary>The setting is readable but does not match the expectation — it did not take.</summary>
    DidNotTake,

    /// <summary>
    /// The setting cannot be read from <see cref="SettingsState"/> (not all firmware
    /// settings are OS-readable). We say "could not verify" — never a false success.
    /// </summary>
    Unreadable,
}

/// <summary>
/// The outcome of one expected setting after the reboot: what we expected, what we
/// observed (<see cref="TriState.Unknown"/> when not readable) and the honest verdict.
/// </summary>
/// <param name="Setting">The firmware setting evaluated.</param>
/// <param name="ExpectedState">The state we expected (carried from the marker).</param>
/// <param name="ObservedState">The freshly re-read state, or <see cref="TriState.Unknown"/> when not readable.</param>
/// <param name="Verdict">Took / DidNotTake / Unreadable — never a fabricated success.</param>
public record BiosSettingOutcome(
    FirmwareSettingKind Setting,
    TriState ExpectedState,
    TriState ObservedState,
    BiosSettingVerdict Verdict);

/// <summary>
/// The closed-loop result returned at the next Windows return. Either every expected
/// setting took (success, marker cleaned up) or at least one did not take / could not
/// be verified — in which case the recovery card is shown and the marker is KEPT (so
/// the loop can run again after the next attempt). Never a false "OK".
/// </summary>
/// <param name="HadPendingChange">
/// False when there was no marker at all (idempotent "nothing was pending" no-op);
/// true when a real pending change was evaluated.
/// </param>
/// <param name="AllTook">
/// True only when <see cref="HadPendingChange"/> and every outcome is
/// <see cref="BiosSettingVerdict.Took"/>. A single DidNotTake or Unreadable makes this
/// false (we do not claim success on an unverifiable setting).
/// </param>
/// <param name="Outcomes">The per-setting outcomes (empty when there was no pending change).</param>
/// <param name="ShowRecoveryCard">
/// True when something did not take or could not be verified → the UI must show the
/// recovery card (<see cref="RecoveryCardKeys"/>). Never true on a clean success.
/// </param>
/// <param name="RecoveryCardKeys">
/// Ordered resx keys for the recovery card (retry one change at a time, Clear-CMOS,
/// Load Optimized Defaults…). Empty on success.
/// </param>
/// <param name="MarkerCleared">
/// True when the marker was removed (clean success). False when it was KEPT (failure /
/// unreadable, so the loop persists across the next attempt) or when nothing was pending.
/// </param>
public record BiosLoopResult(
    bool HadPendingChange,
    bool AllTook,
    IReadOnlyList<BiosSettingOutcome> Outcomes,
    bool ShowRecoveryCard,
    IReadOnlyList<string> RecoveryCardKeys,
    bool MarkerCleared,
    DateTimeOffset? DepartUtc = null)
{
    /// <summary>The idempotent "no marker, nothing was pending" result.</summary>
    public static BiosLoopResult NothingPending { get; } = new(
        HadPendingChange: false,
        AllTook: false,
        Outcomes: Array.Empty<BiosSettingOutcome>(),
        ShowRecoveryCard: false,
        RecoveryCardKeys: Array.Empty<string>(),
        MarkerCleared: false);
}

/// <summary>
/// Owns the on-disk "firmware change pending a reboot" marker. Writes atomically
/// (temp + move, never a torn file) and reads fail-clean: an absent marker is a clean
/// <c>Ok(null)</c> "nothing pending"; a CORRUPT marker is a
/// <see cref="ErrorCodes.BiosPendingChangeCorrupt"/> failure (never trusted, never a
/// throw). Never writes the BIOS — this is an OS file only.
/// </summary>
public interface IPendingBiosChangeStore
{
    /// <summary>
    /// Persists <paramref name="change"/> as the pending marker, atomically. The
    /// timestamp must already come from <see cref="IBiosClock"/> (the store does not
    /// read the clock). Fails clean via <see cref="Result"/> on an invalid change or an
    /// I/O fault; never throws.
    /// </summary>
    Result Save(PendingBiosChange change);

    /// <summary>
    /// Reads the pending marker. <c>Ok(null)</c> when there is none (clean no-op);
    /// <c>Ok(change)</c> when a valid one is present; <c>Fail</c> on a corrupt marker or
    /// an I/O fault. Never throws.
    /// </summary>
    Result<PendingBiosChange?> Load();

    /// <summary>
    /// Removes the pending marker if present (a clean success, or a user dismissal).
    /// Absent marker → <c>Ok</c> (idempotent). Fails clean on an I/O fault; never throws.
    /// </summary>
    Result Clear();
}

/// <summary>
/// The post-reboot closed-loop verifier. Takes the pending marker (via the store) plus
/// the FRESH <see cref="SettingsState"/> re-read by M1 and produces an honest
/// <see cref="BiosLoopResult"/>: per setting Took / DidNotTake / Unreadable. On a clean
/// success it clears the marker; on any DidNotTake / Unreadable it KEEPS the marker and
/// emits the recovery card — never a false success. No marker → an idempotent
/// "nothing pending". Never writes the BIOS.
/// </summary>
public interface IBiosLoopVerifier
{
    /// <summary>
    /// Verifies the pending firmware change (if any) against <paramref name="freshState"/>.
    /// Fails clean via <see cref="Result"/> on a corrupt marker or an I/O fault; never throws.
    /// </summary>
    Result<BiosLoopResult> Verify(SettingsState freshState);
}
