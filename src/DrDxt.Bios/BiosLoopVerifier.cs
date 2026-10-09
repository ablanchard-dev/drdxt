using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// The post-reboot closed-loop verifier. At the next Windows return it loads the
/// pending-change marker (via <see cref="IPendingBiosChangeStore"/>) and compares each
/// expected setting against the FRESH <see cref="SettingsState"/> re-read by M1:
///
///   • match            → <see cref="BiosSettingVerdict.Took"/>;
///   • readable mismatch → <see cref="BiosSettingVerdict.DidNotTake"/>;
///   • not OS-readable   → <see cref="BiosSettingVerdict.Unreadable"/> (honest "could not verify").
///
/// Honesty (decision §9): Every setting took → success + the marker is cleared. ANY
/// DidNotTake or Unreadable → the recovery card is shown and the marker is KEPT (the
/// loop can run again after the next attempt). We never report a false success and
/// Never claim success on an unverifiable setting. No marker → an idempotent no-op.
/// A marker whose machine id does not match this machine (copied from another PC) is
/// never honored — it is treated as nothing-pending and left untouched.
///
/// Never writes the BIOS — it only reads an OS marker + the already-read settings.
/// </summary>
public sealed class BiosLoopVerifier : IBiosLoopVerifier
{
    private readonly IPendingBiosChangeStore _store;
    private readonly IBiosMachineId _machineId;

    public BiosLoopVerifier(IPendingBiosChangeStore store, IBiosMachineId machineId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _machineId = machineId ?? throw new ArgumentNullException(nameof(machineId));
    }

    /// <inheritdoc />
    public Result<BiosLoopResult> Verify(SettingsState freshState)
    {
        if (freshState is null)
            return Result<BiosLoopResult>.Fail(
                ErrorCodes.BiosInputInvalid, "Fresh settings state is null; cannot verify the loop.");

        // Load the marker. A corrupt marker / I/O fault propagates as a clean failure
        // (the store already wrapped the boundary). Absent marker → nothing pending.
        var load = _store.Load();
        if (!load.IsOk)
            return Result<BiosLoopResult>.Fail(load.Error!.Value.Code, load.Error.Value.Message);

        if (load.Value is null)
            return Result<BiosLoopResult>.Ok(BiosLoopResult.NothingPending);

        var change = load.Value;

        // ── Fail-safe machine-id compare (decision §9): a marker copied from another PC
        //    must never be honored here. On a mismatch we treat it as nothing-pending and
        //    leave the foreign marker untouched (not cleared, not evaluated) — we never
        //    claim a foreign change took/failed on this machine. ──
        if (!string.Equals(
                change.MachineId?.Trim(), _machineId.Current?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result<BiosLoopResult>.Ok(BiosLoopResult.NothingPending);

        // ── Evaluate every expected setting honestly. ──
        var outcomes = new List<BiosSettingOutcome>(change.Targets.Count);
        foreach (var target in change.Targets)
        {
            var observed = ReadObserved(freshState, target.Setting);
            // The MEASURED delta wins over the inferred TriState when we have one: the loop's
            // question is "what changed across this reboot", not "what is the state now".
            var verdict = DecideByDelta(target, freshState) ?? Decide(target.ExpectedState, observed);
            outcomes.Add(new BiosSettingOutcome(target.Setting, target.ExpectedState, observed, verdict));
        }

        var allTook = outcomes.Count > 0 && outcomes.All(o => o.Verdict == BiosSettingVerdict.Took);

        if (allTook)
        {
            // Clean success → remove the marker. A clear I/O fault is surfaced (we do
            // not pretend the loop is over if we could not clean up).
            var clear = _store.Clear();
            if (!clear.IsOk)
                return Result<BiosLoopResult>.Fail(clear.Error!.Value.Code, clear.Error.Value.Message);

            return Result<BiosLoopResult>.Ok(new BiosLoopResult(
                HadPendingChange: true,
                AllTook: true,
                Outcomes: outcomes,
                ShowRecoveryCard: false,
                RecoveryCardKeys: Array.Empty<string>(),
                MarkerCleared: true,
                DepartUtc: change.CreatedUtc));
        }

        // Something did not take or could not be verified → recovery card, marker KEPT.
        return Result<BiosLoopResult>.Ok(new BiosLoopResult(
            HadPendingChange: true,
            AllTook: false,
            Outcomes: outcomes,
            ShowRecoveryCard: true,
            RecoveryCardKeys: BiosLoopStringKeys.RecoveryCard,
            MarkerCleared: false,
            DepartUtc: change.CreatedUtc));
    }

    // ── The honest verdict for one setting. ──
    private static BiosSettingVerdict Decide(TriState expected, TriState observed)
    {
        // Not OS-readable (or the fresh read itself is Unknown) → we cannot confirm.
        if (observed == TriState.Unknown)
            return BiosSettingVerdict.Unreadable;

        return observed == expected
            ? BiosSettingVerdict.Took
            : BiosSettingVerdict.DidNotTake;
    }

    // ── The DELTA verdict: did the measured quantity move, and in the expected direction?
    //    Returns null when no delta is available (no scalar captured before, or none readable
    //    now) — the caller then falls back to the TriState compare. We never invent a move.
    //
    //    Why this exists (M3 lot 1): XmpExpo is an INFERENCE labelled "probably" by its own
    //    producer. Comparing it to an expectation compares a guess to a guess — and on a kit
    //    whose JEDEC floor is exceeded in both states it does not move at all while the machine
    //    does. 2133 -> 3800 is a fact. ──
    private static BiosSettingVerdict? DecideByDelta(PendingSettingTarget target, SettingsState state)
    {
        // 0 = « jamais lu », jamais « 0 MHz » — des deux côtés. La garde sur `before` a été
        // ajoutée le 26/08 : elle manquait, et la source qui alimentera ce champ
        // (`SettingsState.MemoryConfiguredMhz`) documente elle-même que 0 signifie non lu.
        // Sans elle, une machine dont la RAM n'a pas été lue donnait before=0, after=3800,
        // donc `Took` — un succès fabriqué depuis une ligne de base inexistante, dans la
        // fonction dont la raison d'être est précisément de refuser ça.
        if (target.ObservedBefore is not { } before || before == 0) return null;

        long after = ObservableNow(state, target.Setting);
        if (after == 0) return null;

        return target.ExpectedState switch
        {
            // Enabling must RAISE the quantity; disabling must LOWER it. No movement at all
            // means the change did not take, whatever the inference says about the state.
            TriState.On => after > before ? BiosSettingVerdict.Took : BiosSettingVerdict.DidNotTake,
            TriState.Off => after < before ? BiosSettingVerdict.Took : BiosSettingVerdict.DidNotTake,
            _ => null,
        };
    }

    // ── The hard number behind a setting, when there is one. 0 = not read. ──
    private static long ObservableNow(SettingsState state, FirmwareSettingKind kind) => kind switch
    {
        FirmwareSettingKind.XmpExpo => state.MemoryConfiguredMhz,
        _ => 0,
    };

    // ── Map a firmware setting to the fresh SettingsState read. Anything we cannot read is
    //    honestly Unknown — the loop reports Unreadable rather than fabricating a state. ──
    private static TriState ReadObserved(SettingsState state, FirmwareSettingKind kind) => kind switch
    {
        FirmwareSettingKind.XmpExpo => state.XmpExpo,
        FirmwareSettingKind.ReBar => state.ReBar,

        // Above 4G Decoding is a ReBAR PREREQUISITE: ReBAR cannot be active without it, so an
        // active ReBAR proves it On. The converse proves nothing (Above4G can be on with ReBAR
        // off), hence Unknown rather than a fabricated Off.
        FirmwareSettingKind.Above4G => state.ReBar == TriState.On ? TriState.On : TriState.Unknown,

        // CSM: filled by M1 from the boot mode. Legacy boot proves CSM active; a UEFI boot does
        // Not prove it inactive. The reader already encodes that asymmetry.
        FirmwareSettingKind.Csm => state.Csm,

        // Still genuinely unreadable, and each for a named reason — not an unfilled switch:
        //   X3dCppc     — needs the CPPC preferred-core routing, not just parked cores.
        //   PcieGen     — needs nvmlDeviceGetCurrPcieLinkGeneration/Width; INvmlNative exposes
        //                 only BAR1/power/temp/driver today.
        //   BiosVersion — not a toggle: a TriState cannot express "1.H0 -> 1.J0". It belongs to
        //                 the delta path with a string comparand, not here.
        _ => TriState.Unknown,
    };
}
