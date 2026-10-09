using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// E3 — GpuPowerLimitTweak.
//
// Caps the GPU power limit (% of TDP) via NVAPI (ApplyNvApiPowerLimitOp) or the
// ADLX power-limit control on AMD. A lower power limit = lower temps and steadier
// 1% lows for ~0 peak-FPS cost on an already-cool desktop (§5). The limit is
// Always clamped to GpuSafeBounds.MinPowerLimitPct — it can never starve the GPU
// below the floor (which would cause stutter, not gains).
//
// Same invariants as E2: desktop dGPU gating, clamp via GpuSafeBounds, capture the
// Real original → serialized inverse op snapshot, apply via the allowlisted typed
// op, revert via the base, fail-clean. No stability test (reducing power can't make
// a stable GPU unstable, unlike a voltage offset).
// -----------------------------------------------------------------------
public sealed class GpuPowerLimitTweak : GpuTweakBase
{
    private readonly INvApiGpuTuning _nvApi;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IGpuTuningTargetSource _targets;

    public GpuPowerLimitTweak(
        INvApiGpuTuning nvApi,
        IAdlxGpuTuning adlx,
        IElevatedClient elevated,
        IGpuTuningTargetSource targets)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _targets = targets;
    }

    // M6 — the live power-limit target. A per-chip profile may have overridden it at scan
    // time (already clamped at the front door); read fresh here and still re-clamped via
    // GpuSafeBounds below before any write (defense in depth).
    private GpuPowerLimitTarget Target => _targets.PowerLimit;

    public override string Id => "gpu.power_limit";
    public override string SummaryKey => "tweak.gpu.power_limit.efficiency";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        GpuGating.HasDesktopDiscreteGpu(hw);

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Clamp to the safe floor — the power limit can never reach the helper
            // below GpuSafeBounds.MinPowerLimitPct.
            var safePct = GpuSafeBounds.ClampPowerLimitPct(Target.PowerLimitPct);

            if (_nvApi.IsAvailable)
                return await ApplyNvidiaAsync(safePct).ConfigureAwait(false);
            if (_adlx.IsAvailable)
                return await ApplyAmdAsync(safePct).ConfigureAwait(false);

            // §22.3: never silently skip. If an AMD GPU is present (a hardware fact from the
            // scanned inventory, independent of the ADLX SDK) but no tuning SDK is wired,
            // surface an explicit honest status.
            if (_targets.IsAmdGpuPresent)
                return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                    "AMD GPU detected: ADLX tuning SDK not available in this build.");

            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Neither NVAPI nor ADLX is available for GPU power-limit.");
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU power-limit apply boundary error: {ex.Message}");
        }
    }

    private async Task<Result<TweakSnapshot>> ApplyNvidiaAsync(int safePct)
    {
        var capture = _nvApi.GetPowerLimitPct(Target.GpuIndex);
        if (!capture.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read original power-limit: {capture.Error?.Code}.");

        var inverse = new ApplyNvApiPowerLimitOp(Target.GpuIndex, capture.Value);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) CAPABILITY PROBE (north-star plan §6.0) — prove the write path is live on
        // This card+driver before touching anything: replay the inverse op (an IDENTITY
        // write of the value just captured — no hardware change) and read it back. A card
        // whose driver reports success and writes nothing is caught here instead of
        // producing a fake "applied". Fail → TWEAK.UNSUPPORTED_ON_THIS_GPU, nothing tuned.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _nvApi.GetPowerLimitPct(Target.GpuIndex), capture.Value,
            "GPU power-limit").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        var pose = new ApplyNvApiPowerLimitOp(Target.GpuIndex, safePct);
        var applied = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // 08/10/2026 : la valeur posée, RELUE (revue n° 11 : un pilote qui arrondit ferait croire au rendu que le joueur
        // l'a changée), pour que la désinstallation rende la limite seulement si elle porte encore la sienne.
        if (_nvApi.GetPowerLimitPct(Target.GpuIndex) is { IsOk: true } reluNv) pose = pose with { PowerLimitPercent = reluNv.Value };
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, PrivilegedOpCodec.Serialize(pose).Value));
    }

    private async Task<Result<TweakSnapshot>> ApplyAmdAsync(int safePct)
    {
        // Resolve the tunable DISCRETE AMD GPU first (iGPU + dGPU: index 0 is often the
        // non-tunable iGPU). The read seam mirrors the AMD harness picker and returns the
        // first index whose GFX tuning reads. Fail-clean (no apply) when none is found.
        var resolved = _adlx.ResolveTunableGpuIndex();
        if (!resolved.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not resolve a tunable AMD GPU: {resolved.Error?.Code}.");
        int gpuIndex = resolved.Value;

        var capture = _adlx.GetPowerLimitPct(gpuIndex);
        if (!capture.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read original power-limit: {capture.Error?.Code}.");

        var inverse = new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.PowerLimit, capture.Value);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) CAPABILITY PROBE (north-star plan §6.0) — prove the write path is live on
        // This card+driver before touching anything: replay the inverse op (an IDENTITY
        // write of the value just captured — no hardware change) and read it back. A card
        // whose driver reports success and writes nothing is caught here instead of
        // producing a fake "applied". Fail → TWEAK.UNSUPPORTED_ON_THIS_GPU, nothing tuned.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _adlx.GetPowerLimitPct(gpuIndex), capture.Value,
            "GPU power-limit").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        // Capture, write AND revert (via snapshot) all target the same resolved physical GPU.
        var pose = new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.PowerLimit, safePct);
        var applied = await Elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        if (_adlx.GetPowerLimitPct(gpuIndex) is { IsOk: true } reluAmd) pose = pose with { IntValue = reluAmd.Value };
        return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, PrivilegedOpCodec.Serialize(pose).Value));
    }
}
