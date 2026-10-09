using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// R2 GPU bench — GpuBench: a measurement-facing wrapper over the deterministic
// compute runner. Turns ONE run into a repeatable, comparable result for the
// tools/r2bench A/B: a throughput SCORE + a stability verdict + a checksum-ok flag.
//
// The score (ElementIterationsPerSecond = ElementCount * IterationsCompleted /
// seconds) is the comparable number: because the workload is identical run-to-run,
// the only thing that moves it is GPU clock/throughput. So: run at stock, apply the
// +offset via the helper (separate UAC step), run again, compare scores cleanly.
//
// Verdict mapping mirrors the probe's fail-safe contract:
//   * Healthy + checksum-ok  → Stable
//   * DeviceLost OR checksum-mismatch → Unstable  ("no TDR ≠ stable")
//   * Error / runner failure → Inconclusive
// It never throws. Read/compute only — no NVAPI, no GPU write, no elevation.
// -----------------------------------------------------------------------

/// <summary>One repeatable bench result: a comparable throughput score plus the
/// stability verdict and whether the deterministic checksum matched the known answer.</summary>
public sealed record GpuBenchResult(
    StabilityOutcome Stability,
    bool ChecksumOk,
    ulong Checksum,
    int IterationsCompleted,
    double ElapsedMilliseconds,
    double ElementIterationsPerSecond);

public sealed class GpuBench
{
    private readonly IGpuComputeRunner _runner;
    private readonly GpuWorkloadSpec _spec;

    public GpuBench(IGpuComputeRunner runner) : this(runner, GpuBenchWorkload.Default) { }

    public GpuBench(IGpuComputeRunner runner, GpuWorkloadSpec spec)
        => (_runner, _spec) = (runner, spec);

    public GpuWorkloadSpec Spec => _spec;

    public Result<GpuBenchResult> Run(int gpuIndex, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var run = _runner.Run(_spec, duration, ct);
            if (!run.IsOk || run.Value is null)
                return Result<GpuBenchResult>.Ok(Inconclusive());

            var o = run.Value;
            switch (o.DeviceStatus)
            {
                case GpuDeviceStatus.DeviceLost:
                    return Result<GpuBenchResult>.Ok(
                        new GpuBenchResult(StabilityOutcome.Unstable, ChecksumOk: false,
                            o.Checksum, o.IterationsCompleted, o.ElapsedMilliseconds, Score(o)));

                case GpuDeviceStatus.Healthy:
                    var ok = o.Checksum == GpuBenchWorkload.ComputeKnownChecksum(_spec);
                    return Result<GpuBenchResult>.Ok(
                        new GpuBenchResult(
                            ok ? StabilityOutcome.Stable : StabilityOutcome.Unstable,
                            ChecksumOk: ok,
                            o.Checksum, o.IterationsCompleted, o.ElapsedMilliseconds, Score(o)));

                default: // Error
                    return Result<GpuBenchResult>.Ok(Inconclusive());
            }
        }
        catch (Exception)
        {
            return Result<GpuBenchResult>.Ok(Inconclusive());
        }
    }

    private double Score(GpuComputeOutcome o)
    {
        var seconds = o.ElapsedMilliseconds / 1000.0;
        if (seconds <= 0) return 0;
        return (double)_spec.ElementCount * o.IterationsCompleted / seconds;
    }

    private static GpuBenchResult Inconclusive()
        => new(StabilityOutcome.Inconclusive, ChecksumOk: false, 0, 0, 0, 0);
}
