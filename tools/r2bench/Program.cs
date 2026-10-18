using System.Diagnostics;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Detection.Gpu.Bench;

// -----------------------------------------------------------------------
// Deterministic GPU bench — a CONTROLLED, repeatable GPU workload that replaces
// noisy live-gameplay A/B. It runs the SAME deterministic compute workload every
// time, so baseline-vs-offset comparison is clean (the only thing that moves the
// score is GPU clock/throughput).
//
// READ/COMPUTE ONLY: no NVAPI, no GPU-tuning write, NON-ELEVATED. Applying the
// offset stays the helper's job (a separate UAC step). See README.md for the A/B.
//
// Usage:  dotnet run --project tools/r2bench -- [gpuIndex] [seconds]
//   gpuIndex  default 0   (informational; the runner uses DXGI's high-performance adapter)
//   seconds   default 5   (bounded load duration)
// -----------------------------------------------------------------------

int gpuIndex = args.Length > 0 && int.TryParse(args[0], out var gi) ? gi : 0;
double seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 5.0;
var duration = TimeSpan.FromSeconds(seconds);

Console.WriteLine("== R2 deterministic GPU bench (read/compute only, no NVAPI, no elevation) ==");
Console.WriteLine($"  gpuIndex = {gpuIndex}   duration = {seconds:0.##}s");

var spec = GpuBenchWorkload.Default;
Console.WriteLine($"  workload : elements={spec.ElementCount}  iterations={spec.Iterations}  seed=0x{spec.Seed:X8}");
Console.WriteLine($"  known-answer checksum = 0x{GpuBenchWorkload.ComputeKnownChecksum(spec):X16}");
Console.WriteLine();

var runner = new VorticeGpuComputeRunner();
var bench = new GpuBench(runner, spec);

var sw = Stopwatch.StartNew();
var result = bench.Run(gpuIndex, duration, CancellationToken.None);
sw.Stop();

if (!result.IsOk || result.Value is null)
{
    Console.WriteLine($"RESULT: bench could not run ({result.Error?.Code}: {result.Error?.Message}).");
    return 2;
}

var r = result.Value;
Console.WriteLine($"  stability        = {r.Stability}");
Console.WriteLine($"  checksum-ok      = {r.ChecksumOk}");
Console.WriteLine($"  checksum         = 0x{r.Checksum:X16}");
Console.WriteLine($"  iterations done  = {r.IterationsCompleted}");
Console.WriteLine($"  gpu elapsed      = {r.ElapsedMilliseconds:0.0} ms   (wall {sw.Elapsed.TotalMilliseconds:0.0} ms)");
Console.WriteLine($"  SCORE            = {r.ElementIterationsPerSecond:0.###e+00} element-iterations/sec");
Console.WriteLine();

switch (r.Stability)
{
    case StabilityOutcome.Stable:
        Console.WriteLine("RESULT: STABLE, checksum verified. SCORE above is the comparable number for the A/B.");
        return 0;
    case StabilityOutcome.Unstable:
        Console.WriteLine("RESULT: UNSTABLE — device lost (TDR) or checksum mismatch (silent corruption). Revert any offset.");
        return 1;
    default:
        Console.WriteLine("RESULT: INCONCLUSIVE — the GPU workload could not run (no FL11 device / D3D11 failure).");
        return 2;
}
