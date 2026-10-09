using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.ElevatedHelper.Execution.NvApi;

// -----------------------------------------------------------------------
// NvApiTuningActions — the real risky NVAPI tuning writes (the m2smoke boundary).
//
// It reuses the SINGLE verified native NVAPI boundary — NvApiNative in DrDxt.GpuNative
// (the reverse-engineered private SetPstates20 / ClientPowerPoliciesSetStatus recipe,
// live-validated on an RTX 4060). There is NO second copy of the function IDs or the
// GET-then-SET recipe in the helper (the divergence trap we swore off): the helper just
// orchestrates over INvApiNative, exactly like ADLXTuningActions over IAdlxNative.
//
// GOLDEN rules (enforced inside NvApiNative):
//   #1 version word = Marshal.SizeOf(struct) | (1 << 16) at runtime — a size mismatch
//      becomes the driver's SAFE IncompatibleStructVersion, never a write.
//   #2 GET-then-SET: read the FULL current struct, mutate only the one field IN PLACE,
//      write it back — never a zero-built write struct.
//   #3 status 0 = Ok; any non-zero → the boundary returns false → Result.Fail here.
//
// Defense-in-depth (this layer): out-of-policy write values are REFUSED here by
// NvApiTuningMath's policy-band rails (which MIRROR the App's GpuSafeBounds and also reject
// int.MinValue and NaN) before the value ever reaches the native boundary — the elevated GPU
// write enforces the safety band itself and never trusts its caller for value bounds, even on
// a DIRECT (non-IPC) call from a smoke tool that bypasses OpAllowlist. Out of band is
// REJECTED, not clamped.
//
// Every method returns a Result and never throws across the IPC. On an AMD / no-NVIDIA box,
// or when a PRIVATE function id is not exported by the installed driver, the boundary returns
// false and the call fails clean (HELPER.OP_FAILED).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class NvApiTuningActions : INvApiTuningActions
{
    private const string Unavailable = "NVIDIA NVAPI unavailable";

    private readonly INvApiNative _native;

    public NvApiTuningActions() : this(new NvApiNative()) { }

    internal NvApiTuningActions(INvApiNative native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    // --- reads (capture-original for a faithful revert) ---

    public Result<int> GetCoreClockOffsetMhz(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            return _native.TryReadCoreOffsetMhz(gpuIndex, out var mhz)
                ? Result<int>.Ok(mhz)
                : Result<int>.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI core-clock offset read failed (no NVIDIA GPU at index, or driver fault).");
        }
        catch (Exception ex)
        {
            // The native boundary already swallows faults (returns false); last-resort guard
            // so an exception never crosses the IPC.
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, $"NVAPI read failed: {ex.GetType().Name}.");
        }
    }

    public Result<int> GetPowerLimitPercent(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            return _native.TryReadPowerLimitPct(gpuIndex, out var pct)
                ? Result<int>.Ok(pct)
                : Result<int>.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI power-limit read failed (no NVIDIA GPU at index, or driver fault).");
        }
        catch (Exception ex)
        {
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, $"NVAPI read failed: {ex.GetType().Name}.");
        }
    }

    // --- writes (GET-then-SET via the leaf; policy band re-checked here) ---

    public Result SetCoreClockOffset(int gpuIndex, int offsetMHz)
    {
        // Policy band enforced at the executor too (no Math.Abs → int.MinValue is safe): the
        // elevated write never trusts its caller for value bounds, even on a DIRECT (non-IPC)
        // call from a smoke tool that bypasses OpAllowlist. Out of band is REJECTED, not clamped.
        if (!NvApiTuningMath.IsCoreOffsetWithinHardRail(offsetMHz))
            return Result.Fail(ErrorCodes.HelperOpFailed,
                $"Refused: core-clock offset {offsetMHz} MHz is outside the GPU safe-bounds policy band " +
                $"[{NvApiTuningMath.MinCoreOffsetMhz}, {NvApiTuningMath.MaxCoreOffsetMhz}] MHz.");
        if (gpuIndex < 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            if (!_native.TrySetCoreClockOffset(gpuIndex, offsetMHz))
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI core-clock write failed (clock not editable, private id absent, or driver fault).");

            return Confirm(offsetMHz, _native.TryReadCoreOffsetMhz(gpuIndex, out var actualMhz),
                actualMhz, "core-clock offset", "MHz");
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"NVAPI write failed: {ex.GetType().Name}.");
        }
    }

    public Result SetPowerLimitPercent(int gpuIndex, float pct)
    {
        // Hard safety rail (rejects NaN / Infinity, which slip past naive comparisons).
        if (!NvApiTuningMath.IsPowerPercentWithinHardRail(pct))
            return Result.Fail(ErrorCodes.HelperOpFailed,
                $"Refused: power-limit {pct}% is outside the hard [{NvApiTuningMath.MinPowerPercent}, {NvApiTuningMath.MaxPowerPercent}]% rail.");
        if (gpuIndex < 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            if (!_native.TrySetPowerLimitPercent(gpuIndex, pct))
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI power-limit write failed (invalid driver bounds or driver fault).");

            return Confirm((int)MathF.Round(pct), _native.TryReadPowerLimitPct(gpuIndex, out var actualPct),
                actualPct, "power limit", "%");
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"NVAPI write failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// A native write returning true means the driver accepted the call, not that the value
    /// LANDED. Observed on a real RTX 4060: a 110% power-limit request returned true and the
    /// card kept 100% — it has no headroom and the driver clamped in silence. Reporting that as
    /// applied would tell a customer we raised their power limit while nothing moved, and would
    /// record a revert value the GPU never held.
    ///
    /// So every write reads back and compares. The comparison is exact on purpose: a driver that
    /// quantises a request would surface here as a mismatch, which is information about that
    /// driver, not a defect to paper over with an invented tolerance.
    ///
    /// A readback that fails is not a success either — "applied but unconfirmed" is exactly the
    /// claim this method exists to stop.
    /// </summary>
    private static Result Confirm(int requested, bool readOk, int actual, string what, string unit)
    {
        if (!readOk)
            return Result.Fail(ErrorCodes.HelperOpFailed,
                $"NVAPI {what} write was accepted but could not be read back, so it is not confirmed.");

        return actual == requested
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed,
                $"NVAPI {what} was not applied: asked {requested}{unit}, the driver holds {actual}{unit} " +
                "(it accepted the call and clamped).");
    }
}
