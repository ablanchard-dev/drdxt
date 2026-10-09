using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// GpuStabilityTester (R2b) — turns a brief GPU load probe into a stability verdict for
// the undervolt tweak (E2). SAFETY-CRITICAL, fail-safe by construction:
//   * Stable      only when the load completed with a healthy device,
//   * Unstable    when the device was lost (TDR) during the load,
//   * Inconclusive for everything else — a probe failure, an unknown result, or a
//                 thrown exception. The undervolt tweak treats Inconclusive as "revert",
//                 so a stability test that can't be sure never green-lights an undervolt.
//
// It never throws. The actual GPU load is the injected IGpuLoadProbe (real D3D11
// implementation in Detection, smoke-validated); this orchestration is pure + unit-tested.
// -----------------------------------------------------------------------
public sealed class GpuStabilityTester : IStabilityTester
{
    // A few seconds is enough for an unstable, freshly-applied offset to TDR, while
    // keeping the apply flow responsive.
    private static readonly TimeSpan LoadDuration = TimeSpan.FromSeconds(3);

    private readonly IGpuLoadProbe _probe;

    public GpuStabilityTester(IGpuLoadProbe probe) => _probe = probe;

    public async Task<Result<StabilityOutcome>> RunAsync(int gpuIndex, CancellationToken ct)
    {
        try
        {
            var load = await _probe.RunBriefLoadAsync(gpuIndex, LoadDuration, ct).ConfigureAwait(false);
            if (!load.IsOk)
                return Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive);

            return load.Value switch
            {
                GpuLoadResult.CompletedDeviceHealthy => Result<StabilityOutcome>.Ok(StabilityOutcome.Stable),
                GpuLoadResult.DeviceLost => Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable),
                _ => Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive),
            };
        }
        catch (Exception)
        {
            // Fail-safe: a probe that blows up (or a cancellation) must never read as
            // Stable. Inconclusive → the tweak reverts.
            return Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive);
        }
    }
}
