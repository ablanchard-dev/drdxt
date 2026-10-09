using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// R2 GPU bench — the THIN D3D11 boundary, behind a seam.
//
// IGpuLoadProbe (GpuSeams.cs) is the contract the stability tester consumes. The
// Real probe (DeterministicGpuLoadProbe) is pure orchestration over this seam:
// it owns the deterministic workload spec + the known-answer checksum + the
// score/device-lost/checksum decision logic, and is fully unit-tested with a FAKE
// IGpuComputeRunner (no GPU needed).
//
// IGpuComputeRunner is the only real-D3D11 surface. Its single job: dispatch a
// BOUNDED, DETERMINISTIC compute workload on the GPU and report back what happened
// (device health + the GPU-computed checksum + how much work completed + wall time).
// The real Vortice/Direct3D11 implementation lives in Detection and is covered by a
// live smoke (like the PresentMon spine), not by unit tests.
//
// Determinism contract: for a given GpuWorkloadSpec the output checksum is identical
// run-to-run on a healthy device (no time-seeded randomness in the result). The
// throughput SCORE varies with GPU clock — that is the whole point of the bench.
// -----------------------------------------------------------------------

/// <summary>Device health observed by the compute runner after the workload.</summary>
public enum GpuDeviceStatus
{
    /// <summary>The device ran the workload and is still healthy (no removed-reason).</summary>
    Healthy = 0,
    /// <summary>GetDeviceRemovedReason() reported HUNG / REMOVED / RESET (a TDR) →
    /// the offset is unstable.</summary>
    DeviceLost = 1,
    /// <summary>The workload could not run at all (device creation failed, FL11
    /// unsupported, dispatch errored) → the probe treats this as CouldNotRun.</summary>
    Error = 2,
}

/// <summary>A fixed, deterministic compute workload request. Same spec ⇒ same GPU work
/// and same expected checksum every run. <paramref name="Seed" /> is a FIXED constant
/// (part of the spec, not time-seeded) so the known-answer is reproducible.</summary>
public sealed record GpuWorkloadSpec(int ElementCount, int Iterations, uint Seed);

/// <summary>What the GPU compute run produced. <see cref="Checksum" /> is a deterministic
/// 64-bit reduction of the output buffer (verified against the CPU known-answer).
/// <see cref="IterationsCompleted" /> and <see cref="ElapsedMilliseconds" /> feed the
/// throughput score. <see cref="DeviceStatus" /> carries TDR / error info.</summary>
public sealed record GpuComputeOutcome(
    GpuDeviceStatus DeviceStatus,
    ulong Checksum,
    int IterationsCompleted,
    double ElapsedMilliseconds);

/// <summary>The THIN real-D3D11 seam: run one deterministic compute workload and report
/// what happened. Returns a Result so a missing/failed device fails CLEAN
/// (TWEAK.SDK_UNAVAILABLE) — it must not throw on a device loss (that is
/// GpuDeviceStatus.DeviceLost on an Ok result). The fake used in unit tests implements
/// this; the real Vortice implementation is the only D3D boundary.</summary>
public interface IGpuComputeRunner
{
    /// <summary>Runs the deterministic compute workload described by <paramref name="spec" />
    /// for up to <paramref name="duration" /> (or until <paramref name="ct" /> trips), then
    /// reports the outcome. Never throws across this boundary in the real impl.</summary>
    Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct);
}
