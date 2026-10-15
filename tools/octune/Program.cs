using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Detection.Gpu.Bench;
using DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// octune — LIVE ascending core-offset sweep on the deterministic perf-meter.
//
// stock → +30 → +60 → +90 → +100 (the GpuSafeBounds band caps at +100): each point is
// N deterministic bench runs (score mean + inter-run margin, checksum-verified); a gain
// only counts when it clears the combined margin AND every run is stable. The sweep
// stops at the first instability and ALWAYS ends at the best proven offset (stock when
// none). Non-elevated; the offset is volatile (a reboot clears it).
//
// Usage: octune [--mem] [perRunSeconds] [runs] [candidates...]
//   core defaults: 5s, 3, 30 60 90 100 (band [-200,+100])
//   --mem sweeps the MEMORY offset instead: defaults 100 200 300 400 500 (band ±500)
// -----------------------------------------------------------------------

bool mem = args.Contains("--mem");
var rest = args.Where(a => a != "--mem" && a != "--bw").ToArray();
double perRunSecs = rest.Length > 0 && double.TryParse(rest[0], out var s) ? s : 5.0;
int runs = rest.Length > 1 && int.TryParse(rest[1], out var n) ? n : 3;
int[] candidates = rest.Length > 2
    ? rest.Skip(2).Select(int.Parse).ToArray()
    : mem ? new[] { 100, 200, 300, 400, 500 } : new[] { 30, 60, 90, 100 };

var domain = mem ? "MEMORY" : "core";
var bandLo = mem ? GpuSafeBounds.MinMemoryOffsetMhz : GpuSafeBounds.MinCoreOffsetMhz;
var bandHi = mem ? GpuSafeBounds.MaxMemoryOffsetMhz : GpuSafeBounds.MaxCoreOffsetMhz;
Console.WriteLine($"== octune — deterministic {domain}-offset sweep (non-elevated) ==");
Console.WriteLine($"  perRun={perRunSecs:0.#}s  runs={runs}  candidates=[{string.Join(",", candidates)}]  band=[{bandLo},{bandHi}]");

var nvapi = new NvApiNative();
if (!nvapi.IsAvailable) { Console.WriteLine("NVAPI unavailable — no NVIDIA GPU."); return 2; }

// --bw: use the write-bandwidth-leaning spec (128 MB buffer) instead of the default
// ALU spec, so a memory-offset sweep can actually see VRAM throughput.
bool bw = args.Contains("--bw");
var spec = bw ? GpuBenchWorkload.BandwidthWrite : GpuBenchWorkload.Default;
if (bw) Console.WriteLine($"  spec = BandwidthWrite (elements={spec.ElementCount}, iters={spec.Iterations}, ~{spec.ElementCount * 4L / (1024 * 1024)} MB)");
var session = new GpuBenchSession(new GpuBench(new VorticeGpuComputeRunner(), spec));

bool Apply(int mhz)
{
    var wrote = mem ? nvapi.TrySetMemoryClockOffset(0, mhz) : nvapi.TrySetCoreClockOffset(0, mhz);
    if (!wrote) return false;
    // Read back: the write only counts if the driver actually holds the value.
    var readOk = mem ? nvapi.TryReadMemoryOffsetMhz(0, out var now) : nvapi.TryReadCoreOffsetMhz(0, out now);
    if (!readOk || now != mhz) return false;
    Console.WriteLine($"  [apply] {domain} offset -> {mhz} MHz");
    return true;
}

DrDxt.Core.Result<GpuBenchAggregate> Measure()
{
    var r = session.Measure(0, TimeSpan.FromSeconds(perRunSecs), runs, CancellationToken.None);
    if (r.IsOk && r.Value is not null)
        Console.WriteLine($"  [meter] score={r.Value.MeanScore:0.###e+00} ±{(double.IsInfinity(r.Value.MarginOfError) ? "∞" : r.Value.MarginOfError.ToString("0.##e+00"))}  stableRuns={r.Value.StableRuns}  allStable={r.Value.AllStable}");
    else
        Console.WriteLine($"  [meter] FAILED: {r.Error?.Code} — {r.Error?.Message}");
    return r;
}

var sweep = OcSweep.Run(Measure, Apply, candidates,
    isInBand: mem ? GpuSafeBounds.IsMemoryOffsetSafe : GpuSafeBounds.IsCoreOffsetSafe);
if (!sweep.IsOk || sweep.Value is null)
{
    Console.WriteLine($"SWEEP FAILED: {sweep.Error?.Code} — {sweep.Error?.Message}");
    return 2;
}

var o = sweep.Value;
Console.WriteLine();
Console.WriteLine("== verdict per step ==");
foreach (var st in o.Steps)
    Console.WriteLine($"  +{st.OffsetMhz,-4} applyOk={st.ApplyOk}  " +
        (st.Aggregate is null ? "(no measure)" : $"score={st.Aggregate.MeanScore:0.###e+00}  -> {st.Verdict}"));

Console.WriteLine();
Console.WriteLine($"STOCK  score = {o.Stock.MeanScore:0.###e+00} ±{o.Stock.MarginOfError:0.##e+00}");
Console.WriteLine($"BEST   offset = +{o.BestOffsetMhz} MHz   gain = {o.GainPct:0.##}%   (GPU left at this offset: {o.EndedAtBest})");
return o.EndedAtBest ? 0 : 1;
