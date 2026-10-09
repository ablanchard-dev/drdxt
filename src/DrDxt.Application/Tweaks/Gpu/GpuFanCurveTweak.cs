using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// E4 — GpuFanCurveTweak.
//
// Applies a GPU fan curve (NVAPI / ADLX) via the allowlisted ApplyGpuFanCurveOp.
// A more aggressive curve keeps the GPU cooler under load → steadier 1% lows for
// ~0 peak-FPS cost (§5). The curve is always clamped by GpuSafeBounds so no point
// at a high temperature can ever run below the minimum safe duty (anti-overheat) —
// the helper re-checks this leaf-exact as defense-in-depth.
//
// Same invariants as E2/E3: desktop dGPU gating, clamp via GpuSafeBounds, capture
// the real original curve → serialized inverse op snapshot, apply via the typed op,
// revert via the base, fail-clean.
// -----------------------------------------------------------------------
public sealed class GpuFanCurveTweak : GpuTweakBase
{
    private readonly INvApiGpuTuning _nvApi;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IGpuTuningTargetSource _targets;
    private readonly GpuFanCurveTarget _target;

    public GpuFanCurveTweak(
        INvApiGpuTuning nvApi,
        IAdlxGpuTuning adlx,
        IElevatedClient elevated,
        IGpuTuningTargetSource targets,
        GpuFanCurveTarget target)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _targets = targets;
        _target = target;
    }

    public override string Id => "gpu.fan_curve";
    public override string SummaryKey => "tweak.gpu.fan_curve.cooler";

    // 24/09/2026 : jamais proposé tant que le helper n'écrit pas de courbe. Son exécuteur rend
    // toujours « not yet available » (`PrivilegedOpExecutor.FanCurveDeferred`) : proposer ce levier,
    // c'était promettre un réglage qui échoue à chaque essai.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) => false;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Read the original curve from whichever SDK is present (for a faithful revert).
            Result<FanCurve> capture;
            if (_nvApi.IsAvailable)
                capture = _nvApi.GetFanCurve(_target.GpuIndex);
            else if (_adlx.IsAvailable)
                capture = _adlx.GetFanCurve(_target.GpuIndex);
            else
            {
                // §22.3: never silently skip. If an AMD GPU is present (a hardware fact from
                // the scanned inventory, independent of the ADLX SDK) but no tuning SDK is
                // wired, surface an explicit honest status.
                if (_targets.IsAmdGpuPresent)
                    return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                        "AMD GPU detected: ADLX tuning SDK not available in this build.");

                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Neither NVAPI nor ADLX is available for GPU fan-curve.");
            }

            if (!capture.IsOk || capture.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original fan curve: {capture.Error?.Code}.");

            // Clamp the requested curve so no hot point is ever silent (anti-overheat).
            var safeCurve = GpuSafeBounds.ClampFanCurve(_target.Curve);

            var inverse = ToOp(_target.GpuIndex, capture.Value);
            var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

            var applied = await Elevated
                .ExecuteAsync(ToOp(_target.GpuIndex, safeCurve), CancellationToken.None)
                .ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU fan-curve apply boundary error: {ex.Message}");
        }
    }

    private static ApplyGpuFanCurveOp ToOp(int gpuIndex, FanCurve curve) =>
        new(gpuIndex, curve.Points.Select(p => new FanCurvePointDto(p.TempC, p.DutyPct)).ToArray());
}
