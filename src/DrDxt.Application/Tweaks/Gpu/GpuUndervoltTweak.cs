using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// E2 — GpuUndervoltTweak.
//
// Option A (master §11): a conservative NEGATIVE core-clock offset (+ memory
// offset) via NVAPI, or the ADLX undervolt control on AMD — not a full V/F curve.
// 24/09/2026 : cette ligne disait « the same clocks at lower voltage ». Faux : ce levier écrit un
// Décalage d'horloge borné à [-200, +100] MHz (GpuSafeBounds), dans un sens ou dans l'autre, et
// aucune tension. Le seul profil livré lui passe +75 MHz : un léger overclock testé. Un vrai
// undervolt (courbe V/F) n'est pas dans ce produit ; l'id garde son nom (clé de persistance).
//
// Sequence (every external boundary wrapped → fail clean, never throws):
//   (1) AppliesTo HARD gating = desktop dGPU only (GpuGating),
//   (2) clamp/validate via GpuSafeBounds — out of bounds → TWEAK.NOT_APPLICABLE,
//   (3) capture the real original offset (read seam) → serialized inverse op,
//   (4) apply the offset via IElevatedClient (allowlisted typed op),
//   (5) STABILITY TEST (IStabilityTester) — if not clearly stable, revert the
//       offset immediately and return TWEAK.UNSTABLE. We never return Ok while the
//       GPU is unstable, and we never keep an unverified undervolt.
//
// RevertAsync (base) restores the captured original offset.
// -----------------------------------------------------------------------
public sealed class GpuUndervoltTweak : GpuTweakBase
{
    // Upper bound for the inline stability probe — a hung probe must not pin the apply thread
    // while the offset is live. Past this the verdict is treated as Inconclusive → revert.
    private static readonly TimeSpan StabilityProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly INvApiGpuTuning _nvApi;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IStabilityTester _stability;
    private readonly IGpuTuningTargetSource _targets;

    public GpuUndervoltTweak(
        INvApiGpuTuning nvApi,
        IAdlxGpuTuning adlx,
        IStabilityTester stability,
        IElevatedClient elevated,
        IGpuTuningTargetSource targets)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _stability = stability;
        _targets = targets;
    }

    // M6 — the live tuning target. A per-chip profile may have overridden it at scan time
    // (already clamped to GpuSafeBounds at the front door); read fresh here and still
    // re-clamped below before any write (defense in depth).
    private GpuTuningTarget Target => _cibleExpert ?? _targets.Tuning;
    private GpuTuningTarget? _cibleExpert;

    public override string Id => "gpu.undervolt";
    public override string SummaryKey => "tweak.gpu.undervolt.efficiency";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        GpuGating.HasDesktopDiscreteGpu(hw);

    // GPU-OC (08/10/2026) : le bail du helper, armé avant l'écriture quand le mode Expert le demande.
    private (string Id, int Secondes)? _bailDemande;
    private HeartbeatSender? _bail;

    /// <summary>Pose avec le bail du helper armé avant l'écriture : si DrDXT s'arrête ou plante, la carte revient
    /// seule. Sans bail, rien n'est écrit. Le battement rendu doit vivre tant que le décalage est gardé.</summary>
    public async Task<Result<(TweakSnapshot Snapshot, HeartbeatSender Bail)>> ApplyAvecBailAsync(string idBail, int secondes,
        GpuTuningTarget cible)
    {
        _bailDemande = (idBail, secondes);
        _bail = null;
        _cibleExpert = cible;
        try
        {
            var r = await ApplyAsync().ConfigureAwait(false);
            if (r.IsOk && _bail is { } bail) return Result<(TweakSnapshot, HeartbeatSender)>.Ok((r.Value!, bail));
            if (_bail is { } orphelin) await orphelin.DisposeAsync().ConfigureAwait(false);
            return Result<(TweakSnapshot, HeartbeatSender)>.Fail(
                r.Error?.Code ?? ErrorCodes.HelperOpFailed, r.Error?.Message ?? "bail non armé : rien n'est gardé");
        }
        finally
        {
            _bailDemande = null;
            _bail = null;
            _cibleExpert = null;
        }
    }

    /// <summary>null = rien à armer ou bail armé ; sinon le refus, et l'écriture n'a pas lieu.</summary>
    private async Task<Result?> ArmerLeBailSiDemandeAsync(PrivilegedOp inverse)
    {
        if (_bailDemande is not { } d) return null;
        var hb = await EstablishAutoRevertLeaseAsync(inverse, d.Id, d.Secondes).ConfigureAwait(false);
        if (!hb.IsOk) return Result.Fail(hb.Error!.Value.Code, "bail du helper refusé, la carte n'est pas touchée : " + hb.Error.Value.Message);
        _bail = hb.Value;
        return null;
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // (2) Clamp/validate first — nothing out of bounds may reach the helper.
            if (!GpuSafeBounds.IsCoreOffsetSafe(Target.CoreOffsetMhz)
                || !GpuSafeBounds.IsMemoryOffsetSafe(Target.MemoryOffsetMhz))
            {
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable,
                    "Requested GPU offset is outside the conservative safe bounds.");
            }

            if (_nvApi.IsAvailable)
                return await ApplyNvidiaAsync().ConfigureAwait(false);
            if (_adlx.IsAvailable)
                return await ApplyAmdAsync().ConfigureAwait(false);

            // §22.3: never silently skip. If an AMD GPU is present (a hardware fact from the
            // scanned inventory, independent of the ADLX SDK) but no tuning SDK is wired,
            // surface an explicit honest status — not a generic error, not a success, not a
            // silent non-applicable.
            if (_targets.IsAmdGpuPresent)
                return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                    "AMD GPU detected: ADLX tuning SDK not available in this build.");

            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Neither NVAPI nor ADLX is available for GPU undervolt.");
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU undervolt apply boundary error: {ex.Message}");
        }
    }

    private async Task<Result<TweakSnapshot>> ApplyNvidiaAsync()
    {
        // (3) Capture the real original offset before writing (faithful revert).
        var core = _nvApi.GetCoreClockOffsetMhz(Target.GpuIndex);
        if (!core.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read original core-clock offset: {core.Error?.Code}.");
        // Memory offset is captured only to populate the inverse op's memory field. DrDXT does
        // Not tune memory — the executor applies the core offset only (there is no memory write).
        // So a failed memory read is NON-FATAL: fall back to 0 ("no memory change to revert")
        // rather than abort a perfectly good core-only undervolt on a GPU whose pstates20 doesn't
        // expose a P0 memory domain. 0 here is "we never changed memory", not a fabricated reading.
        var memRead = _nvApi.GetMemoryClockOffsetMhz(Target.GpuIndex);
        int memOffset = memRead.IsOk ? memRead.Value : 0;

        var inverse = new ApplyNvApiClockOffsetOp(Target.GpuIndex, core.Value, memOffset);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) CAPABILITY PROBE (north-star plan §6.0) — prove the write path is live on
        // This card+driver before touching anything: replay the inverse op (an IDENTITY
        // write of the value just captured — no hardware change) and read it back. A card
        // whose driver reports success and writes nothing is caught here instead of
        // producing a fake "applied". Fail → TWEAK.UNSUPPORTED_ON_THIS_GPU, nothing tuned.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _nvApi.GetCoreClockOffsetMhz(Target.GpuIndex), core.Value,
            "GPU core-clock offset").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        // GPU-OC (08/10/2026) : le bail avant l'écriture, quand le mode Expert le demande.
        if (await ArmerLeBailSiDemandeAsync(inverse).ConfigureAwait(false) is { } refusNv)
            return Result<TweakSnapshot>.Fail(refusNv.Error!.Value.Code, refusNv.Error.Value.Message);

        // (4) Apply the requested offset through the allowlisted typed op.
        var applied = await Elevated
            .ExecuteAsync(
                new ApplyNvApiClockOffsetOp(Target.GpuIndex, Target.CoreOffsetMhz, Target.MemoryOffsetMhz),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // (5) Stability test → revert immediately if not clearly stable.
        return await VerifyStableOrRevertAsync(inverse, snapshotJson.Value, Target.GpuIndex).ConfigureAwait(false);
    }

    private async Task<Result<TweakSnapshot>> ApplyAmdAsync()
    {
        // Resolve the tunable DISCRETE AMD GPU first. On an iGPU + dGPU box (e.g. a Ryzen
        // X3D APU at index 0 + a discrete Radeon at index 1) the user-requested
        // Target.GpuIndex (hardcoded 0) is the non-tunable iGPU — reading/writing it would
        // fail before ever touching the real card. The read seam mirrors the AMD harness
        // picker and returns the first index whose GFX tuning reads. Fail-clean (no apply)
        // when no tunable AMD GPU is found.
        var resolved = _adlx.ResolveTunableGpuIndex();
        if (!resolved.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not resolve a tunable AMD GPU: {resolved.Error?.Code}.");
        int gpuIndex = resolved.Value;

        // ADLX reads/writes absolute MaxFreq (MHz), not an offset. Read the current absolute
        // max clock so we can (a) build the absolute target we write and (b) capture the
        // absolute original as the inverse op. This is symmetric with the power-limit path
        // (which captures + replays an absolute %): apply→revert restores the exact stock
        // MaxFreq. (GetUndervoltValue returns a VOLTAGE and is only a readability probe for
        // ResolveTunableGpuIndex — it must never seed the revert op, the unit bug we fixed.)
        var cur = _adlx.GetGfxMaxFreqMhz(gpuIndex);
        if (!cur.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read current GFX max frequency: {cur.Error?.Code}.");

        // Target.CoreOffsetMhz is the user's conservative OFFSET, already GpuSafeBounds-clamped
        // at the front door (and re-validated in ApplyAsync). The absolute target = current + offset.
        int targetMaxFreqMhz = cur.Value + Target.CoreOffsetMhz;

        // Inverse = the absolute original MaxFreq captured before apply → revert restores it exactly.
        var inverse = new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.Undervolt, cur.Value);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) CAPABILITY PROBE (north-star plan §6.0) — prove the write path is live on
        // This card+driver before touching anything: replay the inverse op (an IDENTITY
        // write of the value just captured — no hardware change) and read it back. A card
        // whose driver reports success and writes nothing is caught here instead of
        // producing a fake "applied". Fail → TWEAK.UNSUPPORTED_ON_THIS_GPU, nothing tuned.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _adlx.GetGfxMaxFreqMhz(gpuIndex), cur.Value,
            "GPU max-frequency").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        if (await ArmerLeBailSiDemandeAsync(inverse).ConfigureAwait(false) is { } refusAmd)
            return Result<TweakSnapshot>.Fail(refusAmd.Error!.Value.Code, refusAmd.Error.Value.Message);
        var applied = await Elevated
            .ExecuteAsync(
                new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.Undervolt, targetMaxFreqMhz),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // Capture, write, revert AND stability all target the same resolved physical GPU.
        return await VerifyStableOrRevertAsync(inverse, snapshotJson.Value, gpuIndex).ConfigureAwait(false);
    }

    /// <summary>Runs the stability test; if the GPU is not clearly STABLE, replays
    /// the captured-original inverse op to revert immediately and returns
    /// TWEAK.UNSTABLE. Only a clean Stable verdict yields Ok with the snapshot.
    /// If the revert call itself FAILS, we never claim it was reverted: the message
    /// honestly says the automatic revert failed and points at the System Restore
    /// point (the GPU is still undervolted + unstable).</summary>
    private async Task<Result<TweakSnapshot>> VerifyStableOrRevertAsync(PrivilegedOp inverse, string snapshotJson, int gpuIndex)
    {
        // Bound the inline stability probe: a hung / misbehaving probe must never pin the
        // apply thread with the offset live. A timeout (OperationCanceledException) or any
        // probe fault is treated as not stable (Inconclusive) → we fall through and revert
        // immediately, consistent with the existing fail-safe (only a clean Stable verdict
        // keeps the offset). Strictly safer than awaiting with CancellationToken.None, where
        // a probe throw/hang would skip the revert and leave the GPU undervolted.
        bool stable;
        try
        {
            using var timeout = new CancellationTokenSource(StabilityProbeTimeout);
            var stability = await _stability.RunAsync(gpuIndex, timeout.Token).ConfigureAwait(false);
            stable = stability.IsOk && stability.Value == StabilityOutcome.Stable;
        }
        catch (Exception)
        {
            stable = false; // timeout or harness fault → Inconclusive → revert below.
        }

        if (stable)
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson));

        // Unstable / inconclusive / harness error → revert the offset right now AND
        // check the result. A discarded revert result could leave the GPU undervolted
        // + unstable while we falsely claim it was restored.
        Result revert;
        try
        {
            revert = await Elevated.ExecuteAsync(inverse, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            revert = Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }

        if (!revert.IsOk)
        {
            // Honest, distinct failure: do not pretend it was reverted. 08/10/2026 (revue n° 13) : un code a part,
            // TweakPartiallyApplied, pour que le journal reste et que l'ecran ne dise jamais « rendue ».
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakPartiallyApplied,
                "GPU undervolt failed its stability test AND the automatic revert failed " +
                $"({revert.Error?.Code}: {revert.Error?.Message}): the GPU may still be undervolted; " +
                "use the System Restore point created before this change to restore stock clocks.");
        }

        return Result<TweakSnapshot>.Fail(ErrorCodes.TweakUnstable,
            "GPU undervolt failed its stability test and was reverted immediately.");
    }
}
