using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxGpuTuningReaderTests — fake-based unit tests for AdlxGpuTuningReader.
//
// ALL tests use FakeAdlxNative — no native ADLX calls, no AMD GPU needed.
// The test suite pins:
//   1. FAIL-CLEAN: when native is unavailable, every read returns Fail.
//   2. PASS-THROUGH: when native succeeds, reader maps raw reads to Result.Ok.
//   3. ERROR CODES: all failures carry ErrorCodes.TweakSdkUnavailable.
//   4. INPUT GUARD: negative GPU index → Fail before any native call.
// -----------------------------------------------------------------------
public sealed class AdlxGpuTuningReaderTests
{
    // ── IsAvailable ──────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_is_false_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_is_true_when_native_available()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        sut.IsAvailable.Should().BeTrue();
    }

    // ── GetUndervoltValue ─────────────────────────────────────────────────

    [Fact]
    public void GetUndervoltValue_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.GetUndervoltValue(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetUndervoltValue_returns_ok_with_volt_mv_when_gfx_read_succeeds()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable = true,
            GfxSuccess  = true,
            GfxReads    = new AdlxGfxReads { VoltMv = 950 },
        };
        var r = new AdlxGpuTuningReader(fake).GetUndervoltValue(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(950);
    }

    [Fact]
    public void GetUndervoltValue_returns_fail_when_gfx_read_fails()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, GfxSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).GetUndervoltValue(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetUndervoltValue_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        var r   = sut.GetUndervoltValue(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── ResolveTunableGpuIndex (T4 — discrete-GPU selection) ──────────────

    [Fact]
    public void ResolveTunableGpuIndex_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.ResolveTunableGpuIndex();
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void ResolveTunableGpuIndex_returns_zero_when_first_index_is_tunable()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, GfxSuccess = true };
        var r    = new AdlxGpuTuningReader(fake).ResolveTunableGpuIndex();
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(0);
    }

    [Fact]
    public void ResolveTunableGpuIndex_skips_unsupported_igpu_and_picks_discrete_card()
    {
        // 2-GPU box: iGPU at index 0 is not tunable (read fails); the discrete AMD card
        // at index 1 reads OK → the resolver must select index 1, not the iGPU at 0.
        var fake = new FakeAdlxNative
        {
            IsAvailable        = true,
            GfxSuccessForIndex = i => i == 1,
        };
        var r = new AdlxGpuTuningReader(fake).ResolveTunableGpuIndex();
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(1);
    }

    [Fact]
    public void ResolveTunableGpuIndex_fails_clean_when_no_index_reads()
    {
        // 0-tunable-GPU box: no index's GFX tuning reads → fail-clean, never an index.
        var fake = new FakeAdlxNative { IsAvailable = true, GfxSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).ResolveTunableGpuIndex();
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void ResolveTunableGpuIndex_never_throws_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var act = () => sut.ResolveTunableGpuIndex();
        act.Should().NotThrow();
    }

    // ── GetPowerLimitPct ──────────────────────────────────────────────────

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_ok_with_pct_when_power_read_succeeds()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable  = true,
            PowerSuccess = true,
            PowerReads   = new AdlxPowerReads { PowerLimitPct = 90 },
        };
        var r = new AdlxGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(90);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_power_read_fails()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, PowerSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        var r   = sut.GetPowerLimitPct(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── GetFanCurve ───────────────────────────────────────────────────────

    [Fact]
    public void GetFanCurve_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetFanCurve_returns_ok_with_mapped_points_when_fan_read_succeeds()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable = true,
            FanSuccess  = true,
            FanReads    = new AdlxFanReads
            {
                Points = new[]
                {
                    new AdlxFanPoint { TempC = 60, SpeedPct = 50 },
                    new AdlxFanPoint { TempC = 80, SpeedPct = 75 },
                },
            },
        };
        var r = new AdlxGpuTuningReader(fake).GetFanCurve(0);
        r.IsOk.Should().BeTrue();
        r.Value!.Points.Should().HaveCount(2);
        r.Value.Points[0].Should().Be(new FanCurvePoint(60, 50));
        r.Value.Points[1].Should().Be(new FanCurvePoint(80, 75));
    }

    [Fact]
    public void GetFanCurve_returns_fail_when_fan_read_fails()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, FanSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetFanCurve_returns_empty_curve_when_points_are_null()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable = true,
            FanSuccess  = true,
            FanReads    = new AdlxFanReads { Points = null },
        };
        var r = new AdlxGpuTuningReader(fake).GetFanCurve(0);
        r.IsOk.Should().BeTrue();
        r.Value!.Points.Should().BeEmpty();
    }

    [Fact]
    public void GetFanCurve_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        var r   = sut.GetFanCurve(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── Native unavailable never throws ───────────────────────────────────

    [Fact]
    public void All_reads_on_unavailable_native_never_throw()
    {
        var sut  = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var act1 = () => sut.GetUndervoltValue(0);
        var act2 = () => sut.GetPowerLimitPct(0);
        var act3 = () => sut.GetFanCurve(0);
        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();
    }
}

// ────────────────────────────────────────────────────────────────────────
// FakeAdlxNative — in-process substitute for unit tests.
// Never calls amdadlx64.dll; fully configurable per test.
//
// Write safety guarantee: the Set* properties and LastSet* fields are the
// Only way write calls are observed. The real SetGPUMaxFrequency / SetPowerLimit
// P/Invokes are never reached — amdadlx64.dll is not loaded in test processes.
// ────────────────────────────────────────────────────────────────────────
internal sealed class FakeAdlxNative : IAdlxNative
{
    public bool IsAvailable { get; set; }

    // ── read state ────────────────────────────────────────────────────────

    // Graphics tuning
    public bool         GfxSuccess { get; set; } = true;
    public AdlxGfxReads GfxReads   { get; set; }

    /// <summary>Optional per-index override for GFX read success — models an iGPU + dGPU
    /// box where only the discrete card's index reads. When null, falls back to
    /// <see cref="GfxSuccess"/> for every index.</summary>
    public Func<int, bool>? GfxSuccessForIndex { get; set; }

    // Power tuning
    public bool           PowerSuccess { get; set; } = true;
    public AdlxPowerReads PowerReads   { get; set; }

    // Fan tuning
    public bool         FanSuccess { get; set; } = true;
    public AdlxFanReads FanReads   { get; set; }

    public bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads)
    {
        reads = GfxReads;
        return GfxSuccessForIndex?.Invoke(gpuIndex) ?? GfxSuccess;
    }

    public bool TryReadPower(int gpuIndex, out AdlxPowerReads reads)
    {
        reads = PowerReads;
        return PowerSuccess;
    }

    public bool TryReadFan(int gpuIndex, out AdlxFanReads reads)
    {
        reads = FanReads;
        return FanSuccess;
    }

    // ── write state ───────────────────────────────────────────────────────
    // These fields record and control write behaviour without touching any driver.

    /// <summary>Controls whether TrySetGfx returns true (default) or false.</summary>
    public bool SetGfxSuccess { get; set; } = true;

    /// <summary>What TrySetGfx returns in 'original' — defaults to GfxReads so tests
    /// can configure a single source of truth for current GFX state.</summary>
    public AdlxGfxReads SetGfxOriginal { get; set; }

    /// <summary>The (MaxFreqMhz, VoltMv) tuple last passed to TrySetGfx, or null if
    /// TrySetGfx was never called. Use in tests to assert what was written.</summary>
    public (int MaxFreqMhz, int VoltMv)? LastSetGfx { get; private set; }

    /// <summary>Total number of TrySetGfx calls (use to assert write was not called).</summary>
    public int SetGfxCallCount { get; private set; }

    /// <summary>Controls whether TrySetPowerLimit returns true (default) or false.</summary>
    public bool SetPowerSuccess { get; set; } = true;

    /// <summary>What TrySetPowerLimit returns in 'original'.</summary>
    public AdlxPowerReads SetPowerOriginal { get; set; }

    /// <summary>The power-limit percent last passed to TrySetPowerLimit, or null.</summary>
    public int? LastSetPower { get; private set; }

    /// <summary>Total number of TrySetPowerLimit calls.</summary>
    public int SetPowerCallCount { get; private set; }

    public bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original)
    {
        original = SetGfxOriginal;
        SetGfxCallCount++;
        if (!IsAvailable || !SetGfxSuccess) return false;
        LastSetGfx = (newMaxFreqMhz, newVoltMv);
        return true;
    }

    public bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original)
    {
        original = SetPowerOriginal;
        SetPowerCallCount++;
        if (!IsAvailable || !SetPowerSuccess) return false;
        LastSetPower = newPowerLimitPct;
        return true;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────
    // Synthesises a probe from the fake's flags so tests can exercise the
    // diagnostic surface without amdadlx64.dll.
    public IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex)
    {
        if (!IsAvailable)
            return new[] { new GpuProbeStep("adlx-init", false, "fake: ADLX unavailable") };
        if (!GfxSuccess)
            return new[]
            {
                new GpuProbeStep("adlx-init", true),
                new GpuProbeStep("read-gfx-v2", false, "fake: gfx read failed"),
            };
        return new[]
        {
            new GpuProbeStep("adlx-init", true),
            new GpuProbeStep("read-gfx-v2", true, "fake: ok"),
        };
    }
}
