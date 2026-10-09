using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxGpuTuningWriterTests — fake-based unit tests for AdlxGpuTuningWriter.
//
// No-real-write guarantee: every test uses FakeAdlxNative (defined in
// AdlxGpuTuningReaderTests.cs, same assembly). The amdadlx64.dll P/Invoke
// boundary is never reached. The FakeAdlxNative.LastSetGfx / LastSetPower
// fields are the only observable side-effects of write calls.
//
// Invariants pinned:
//   W1.  IsAvailable=false  → Fail, zero set calls
//   W2.  Negative gpuIndex  → Fail, zero set calls
//   W3.  Bounds-clamping    → requested offset clamped before reaching native
//   W4.  Validate-then-set  → out-of-hardware-range → Fail, no set
//   W5.  Get-then-set order → TryReadGfx called before TrySetGfx
//   W6.  Set is actually called (happy path)
//   W7.  Returned snapshot  → contains original (pre-write) MaxFreq + Volt
//   W8.  RevertGfx          → calls TrySetGfx with the original values
//   W9.  Power: clamp + validate + set + snapshot
//   W10. RevertPower        → calls TrySetPowerLimit with original value
//   W11. SetGfx Fail        → writer returns Fail (driver rejected)
//   W12. All methods on unavailable native never throw
// -----------------------------------------------------------------------
public sealed class AdlxGpuTuningWriterTests
{
    // ── shared test data ──────────────────────────────────────────────────

    /// <summary>Builds a FakeAdlxNative pre-wired with sane current GFX state:
    /// MaxFreq=2700 MHz (range [500,3000]), Volt=0 mV (range [-200,100]),
    /// SetGfxOriginal mirrors GfxReads so that the "original" returned by
    /// TrySetGfx matches the values the writer read via TryReadGfx.</summary>
    private static FakeAdlxNative MakeGfxFake(
        int currentMaxFreq = 2700,
        int voltMv         = 0,
        int freqRangeMin   = 500,
        int freqRangeMax   = 3000,
        int voltRangeMin   = -200,
        int voltRangeMax   = 100)
    {
        var reads = new AdlxGfxReads
        {
            MaxFreqMhz   = currentMaxFreq,
            MaxFreqRange = new AdlxIntRange { Min = freqRangeMin, Max = freqRangeMax, Step = 1 },
            MinFreqMhz   = 500,
            MinFreqRange = new AdlxIntRange { Min = 300, Max = 1000, Step = 1 },
            VoltMv       = voltMv,
            VoltRange    = new AdlxIntRange { Min = voltRangeMin, Max = voltRangeMax, Step = 1 },
        };
        return new FakeAdlxNative
        {
            IsAvailable    = true,
            GfxSuccess     = true,
            GfxReads       = reads,
            SetGfxOriginal = reads,    // original = current (consistent get-then-set)
            SetGfxSuccess  = true,
        };
    }

    /// <summary>Builds a FakeAdlxNative wired for power tests:
    /// PowerLimitPct=100 (range [60,120]).</summary>
    private static FakeAdlxNative MakePowerFake(
        int currentPct  = 100,
        int rangeMin    = 60,
        int rangeMax    = 120)
    {
        var reads = new AdlxPowerReads
        {
            PowerLimitPct = currentPct,
            PowerRange    = new AdlxIntRange { Min = rangeMin, Max = rangeMax, Step = 1 },
        };
        return new FakeAdlxNative
        {
            IsAvailable      = true,
            PowerSuccess     = true,
            PowerReads       = reads,
            SetPowerOriginal = reads,
            SetPowerSuccess  = true,
        };
    }

    // ── W1: IsAvailable=false → Fail, zero set calls ──────────────────────

    [Fact]
    public void ApplyGfxOffset_returns_fail_when_native_unavailable()
    {
        var fake = MakeGfxFake();
        fake.IsAvailable = false;
        var sut = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, -15, 0);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        fake.SetGfxCallCount.Should().Be(0, "no write when ADLX unavailable");
    }

    [Fact]
    public void ApplyPowerLimit_returns_fail_when_native_unavailable()
    {
        var fake = MakePowerFake();
        fake.IsAvailable = false;
        var sut = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyPowerLimit(0, 95);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        fake.SetPowerCallCount.Should().Be(0);
    }

    [Fact]
    public void RevertGfx_returns_fail_when_native_unavailable()
    {
        var fake = MakeGfxFake();
        fake.IsAvailable = false;
        var sut = new AdlxGpuTuningWriter(fake);

        var r = sut.RevertGfx(0, new AdlxGfxSnapshot(2700, 0));

        r.IsOk.Should().BeFalse();
        fake.SetGfxCallCount.Should().Be(0);
    }

    // ── W2: Negative gpuIndex → Fail, zero set calls ─────────────────────

    [Fact]
    public void ApplyGfxOffset_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningWriter(MakeGfxFake());
        var r   = sut.ApplyGfxOffset(-1, -15, 0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void ApplyPowerLimit_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningWriter(MakePowerFake());
        var r   = sut.ApplyPowerLimit(-1, 95);
        r.IsOk.Should().BeFalse();
    }

    // ── W3: Bounds-clamping ───────────────────────────────────────────────

    [Fact]
    public void ApplyGfxOffset_clamps_freq_offset_to_GpuSafeBounds_before_native_call()
    {
        // Request +300 MHz — exceeds GpuSafeBounds.MaxCoreOffsetMhz (100).
        // Clamped to +100. Current = 2600. Target = 2700 — within [500, 3000].
        var fake = MakeGfxFake(currentMaxFreq: 2600);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, maxFreqOffsetMhz: 300, voltOffsetMv: 0);

        r.IsOk.Should().BeTrue("clamped offset fits in hardware range");
        // The write must have used the CLAMPED value, not the raw +300
        fake.LastSetGfx.Should().NotBeNull();
        fake.LastSetGfx!.Value.MaxFreqMhz.Should().Be(2600 + GpuSafeBounds.MaxCoreOffsetMhz,
            "offset clamped to MaxCoreOffsetMhz before native call");
    }

    [Fact]
    public void ApplyGfxOffset_clamps_negative_freq_offset_to_GpuSafeBounds()
    {
        // Request -500 MHz — below GpuSafeBounds.MinCoreOffsetMhz (-200).
        // Clamped to -200. Current = 2700. Target = 2500.
        var fake = MakeGfxFake(currentMaxFreq: 2700);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, maxFreqOffsetMhz: -500, voltOffsetMv: 0);

        r.IsOk.Should().BeTrue();
        fake.LastSetGfx!.Value.MaxFreqMhz.Should().Be(2700 + GpuSafeBounds.MinCoreOffsetMhz,
            "offset clamped to MinCoreOffsetMhz");
    }

    [Fact]
    public void ApplyPowerLimit_clamps_below_safe_floor_to_MinPowerLimitPct()
    {
        // Request 10% — well below GpuSafeBounds.MinPowerLimitPct (60).
        // Clamped to 60. Hardware range [60, 120] — within range.
        var fake = MakePowerFake(rangeMin: 60, rangeMax: 120);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyPowerLimit(0, 10);

        r.IsOk.Should().BeTrue();
        fake.LastSetPower.Should().Be(GpuSafeBounds.MinPowerLimitPct,
            "power clamped to safe floor");
    }

    // ── W4: Validate-then-set — out-of-hardware-range → Fail, no write ───

    [Fact]
    public void ApplyGfxOffset_fails_when_clamped_target_exceeds_hardware_max()
    {
        // Current = 2950, hardware max = 3000, clamped offset = +100 → target 3050 > 3000.
        var fake = MakeGfxFake(currentMaxFreq: 2950, freqRangeMax: 3000);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, maxFreqOffsetMhz: 100, voltOffsetMv: 0);

        r.IsOk.Should().BeFalse("target exceeds hardware max-freq range");
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        fake.SetGfxCallCount.Should().Be(0, "validate-then-set: no write when range violated");
    }

    [Fact]
    public void ApplyGfxOffset_fails_when_clamped_target_is_below_hardware_min()
    {
        // Current = 700, hardware min = 600, offset -200 → target 500 < 600.
        var fake = MakeGfxFake(currentMaxFreq: 700, freqRangeMin: 600);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, maxFreqOffsetMhz: -200, voltOffsetMv: 0);

        r.IsOk.Should().BeFalse("target below hardware min-freq range");
        fake.SetGfxCallCount.Should().Be(0);
    }

    [Fact]
    public void ApplyPowerLimit_fails_when_clamped_target_exceeds_hardware_range()
    {
        // After clamping to SafeBounds (already at 100%), hardware max is only 90%.
        var fake = MakePowerFake(rangeMin: 60, rangeMax: 90);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyPowerLimit(0, 100);

        r.IsOk.Should().BeFalse("clamped 100% exceeds hardware max 90%");
        fake.SetPowerCallCount.Should().Be(0);
    }

    // ── W5 / W6: Get-then-set order + set actually called (happy path) ────

    [Fact]
    public void ApplyGfxOffset_reads_current_then_calls_TrySetGfx()
    {
        // -15 MHz offset. Current = 2700, target = 2685. Hardware range [500,3000] → OK.
        var fake = MakeGfxFake(currentMaxFreq: 2700);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, maxFreqOffsetMhz: -15, voltOffsetMv: 0);

        r.IsOk.Should().BeTrue();
        // TryReadGfx must have been called (GfxSuccess=true means it returned ok)
        // TrySetGfx must have been called exactly once
        fake.SetGfxCallCount.Should().Be(1, "exactly one set call in happy path");
        fake.LastSetGfx.Should().NotBeNull();
        fake.LastSetGfx!.Value.MaxFreqMhz.Should().Be(2685, "2700 + (-15)");
        fake.LastSetGfx!.Value.VoltMv.Should().Be(0, "volt offset = 0 → same as current");
    }

    [Fact]
    public void ApplyPowerLimit_reads_current_then_calls_TrySetPowerLimit()
    {
        var fake = MakePowerFake(currentPct: 100, rangeMin: 60, rangeMax: 120);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyPowerLimit(0, 95);

        r.IsOk.Should().BeTrue();
        fake.SetPowerCallCount.Should().Be(1);
        fake.LastSetPower.Should().Be(95);
    }

    // ── W7: Returned snapshot contains original pre-write values ─────────

    [Fact]
    public void ApplyGfxOffset_returns_snapshot_with_original_MaxFreq_and_Volt()
    {
        // SetGfxOriginal = {MaxFreqMhz=2700, VoltMv=0} — the pre-write state
        var fake = MakeGfxFake(currentMaxFreq: 2700, voltMv: 0);
        // SetGfxOriginal already set to match GfxReads by MakeGfxFake
        var sut = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, -15, 0);

        r.IsOk.Should().BeTrue();
        r.Value.MaxFreqMhz.Should().Be(2700, "snapshot holds original (pre-write) MaxFreq");
        r.Value.VoltMv.Should().Be(0, "snapshot holds original (pre-write) Volt");
    }

    [Fact]
    public void ApplyPowerLimit_returns_snapshot_with_original_power_pct()
    {
        var fake = MakePowerFake(currentPct: 100);
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyPowerLimit(0, 95);

        r.IsOk.Should().BeTrue();
        r.Value.PowerLimitPct.Should().Be(100, "snapshot holds original 100%");
    }

    // ── W8: RevertGfx passes original values to TrySetGfx ────────────────

    [Fact]
    public void RevertGfx_calls_TrySetGfx_with_original_MaxFreq_and_Volt()
    {
        var fake     = MakeGfxFake();
        var sut      = new AdlxGpuTuningWriter(fake);
        var snapshot = new AdlxGfxSnapshot(MaxFreqMhz: 2700, VoltMv: 0);

        var r = sut.RevertGfx(0, snapshot);

        r.IsOk.Should().BeTrue();
        fake.SetGfxCallCount.Should().Be(1);
        fake.LastSetGfx!.Value.MaxFreqMhz.Should().Be(2700);
        fake.LastSetGfx!.Value.VoltMv.Should().Be(0);
    }

    [Fact]
    public void RevertGfx_returns_fail_when_TrySetGfx_fails()
    {
        var fake = MakeGfxFake();
        fake.SetGfxSuccess = false;
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.RevertGfx(0, new AdlxGfxSnapshot(2700, 0));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── W10: RevertPower passes original to TrySetPowerLimit ─────────────

    [Fact]
    public void RevertPower_calls_TrySetPowerLimit_with_original_pct()
    {
        var fake     = MakePowerFake();
        var sut      = new AdlxGpuTuningWriter(fake);
        var snapshot = new AdlxPowerSnapshot(PowerLimitPct: 100);

        var r = sut.RevertPower(0, snapshot);

        r.IsOk.Should().BeTrue();
        fake.SetPowerCallCount.Should().Be(1);
        fake.LastSetPower.Should().Be(100);
    }

    // ── W11: Driver rejects set → writer returns Fail ─────────────────────

    [Fact]
    public void ApplyGfxOffset_returns_fail_when_TrySetGfx_fails()
    {
        var fake = MakeGfxFake();
        fake.SetGfxSuccess = false;
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyGfxOffset(0, -15, 0);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void ApplyPowerLimit_returns_fail_when_TrySetPowerLimit_fails()
    {
        var fake = MakePowerFake();
        fake.SetPowerSuccess = false;
        var sut  = new AdlxGpuTuningWriter(fake);

        var r = sut.ApplyPowerLimit(0, 95);

        r.IsOk.Should().BeFalse();
    }

    // ── W12: All methods on unavailable native never throw ────────────────

    [Fact]
    public void All_write_methods_on_unavailable_native_never_throw()
    {
        var fake = MakeGfxFake();
        fake.IsAvailable = false;
        var sut = new AdlxGpuTuningWriter(fake);
        var snapshot = new AdlxGfxSnapshot(2700, 0);
        var pwrSnap  = new AdlxPowerSnapshot(100);

        ((Action)(() => sut.ApplyGfxOffset(0, -15, 0))).Should().NotThrow();
        ((Action)(() => sut.RevertGfx(0, snapshot))).Should().NotThrow();
        ((Action)(() => sut.ApplyPowerLimit(0, 95))).Should().NotThrow();
        ((Action)(() => sut.RevertPower(0, pwrSnap))).Should().NotThrow();
    }

    // ── Micro-write scenario: smallest step, volt unchanged ───────────────

    [Fact]
    public void MicroWrite_minus15MHz_volt_zero_writes_then_revert_restores()
    {
        // Full micro-write → revert cycle, entirely in-process via fake.
        var fake = MakeGfxFake(currentMaxFreq: 2700, voltMv: 0);
        var sut  = new AdlxGpuTuningWriter(fake);

        // Apply
        var applyResult = sut.ApplyGfxOffset(0, -15, 0);
        applyResult.IsOk.Should().BeTrue();
        fake.LastSetGfx!.Value.MaxFreqMhz.Should().Be(2685, "micro write: 2700 - 15");

        // Revert using the returned snapshot
        var revertResult = sut.RevertGfx(0, applyResult.Value);
        revertResult.IsOk.Should().BeTrue();
        // The revert call should restore to original (2700 MHz, 0 mV)
        fake.LastSetGfx!.Value.MaxFreqMhz.Should().Be(2700, "revert restores original MaxFreq");
        fake.LastSetGfx!.Value.VoltMv.Should().Be(0, "revert restores original Volt");
        fake.SetGfxCallCount.Should().Be(2, "apply + revert = 2 set calls");
    }
}
