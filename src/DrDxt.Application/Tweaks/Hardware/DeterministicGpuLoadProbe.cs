using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// R2 GPU bench — the real IGpuLoadProbe: Pure orchestration over the thin
// IGpuComputeRunner D3D11 seam. It runs a bounded, deterministic GPU compute
// workload and maps the outcome to GpuLoadResult, fail-safe by construction:
//
//   * Healthy device + checksum == known answer → CompletedDeviceHealthy
//   * DeviceLost (TDR)                           → DeviceLost
//   * Healthy device but checksum MISMATCH        → DeviceLost  (silent corruption;
//                                                    per the research "no TDR ≠ stable")
//   * runner Error / Fail / throws               → CouldNotRun (→ Inconclusive → revert)
//
// It never throws across the IGpuLoadProbe boundary. The known-answer is computed
// from the same workload spec the runner is asked to execute, so the checksum check
// is a true known-answer test. The only un-unit-testable surface is the real D3D11
// IGpuComputeRunner (live smoke).
// -----------------------------------------------------------------------
public sealed class DeterministicGpuLoadProbe : IGpuLoadProbe
{
    private readonly IGpuComputeRunner _runner;
    private readonly GpuWorkloadSpec _spec;

    public DeterministicGpuLoadProbe(IGpuComputeRunner runner) : this(runner, GpuBenchWorkload.Default) { }

    public DeterministicGpuLoadProbe(IGpuComputeRunner runner, GpuWorkloadSpec spec)
        => (_runner, _spec) = (runner, spec);

    public Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var run = _runner.Run(_spec, duration, ct);
            if (!run.IsOk || run.Value is null)
                return Task.FromResult(Result<GpuLoadResult>.Ok(GpuLoadResult.CouldNotRun));

            var outcome = run.Value;
            var result = outcome.DeviceStatus switch
            {
                GpuDeviceStatus.DeviceLost => GpuLoadResult.DeviceLost,
                GpuDeviceStatus.Error => GpuLoadResult.CouldNotRun,
                GpuDeviceStatus.Healthy =>
                    outcome.Checksum == GpuBenchWorkload.ComputeKnownChecksum(_spec)
                        ? GpuLoadResult.CompletedDeviceHealthy
                        : GpuLoadResult.DeviceLost,   // silent corruption ⇒ unstable
                _ => GpuLoadResult.CouldNotRun,
            };
            return Task.FromResult(Result<GpuLoadResult>.Ok(result));
        }
        catch (Exception)
        {
            // Fail-safe: a runner that blows up (or cancellation) must never read as a
            // healthy completion. CouldNotRun → Inconclusive → the tweak reverts.
            return Task.FromResult(Result<GpuLoadResult>.Ok(GpuLoadResult.CouldNotRun));
        }
    }
}
