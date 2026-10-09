using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Nvapi;

// -----------------------------------------------------------------------
// NvApiGpuTuningReaderTests — fake-based unit tests for NvApiGpuTuningReader.
//
// ALL tests use FakeNvApiNative — no native NVAPI calls, no NVIDIA GPU needed
// (nvapi64.dll is never loaded in a test process). Mirrors the ADLX reader suite:
//   1. FAIL-CLEAN: when native is unavailable, every read returns Fail (never throws).
//   2. PASS-THROUGH: when native succeeds, reader maps the raw read to Result.Ok.
//   3. Real memory read: core AND memory offsets are each real distinct reads — the
//      memory read is not a fake 0 (ApplyNvidiaAsync fails the undervolt if it can't
//      read the memory offset, so a placebo here would defeat NVIDIA undervolt).
//   4. ERROR CODES: all failures carry ErrorCodes.TweakSdkUnavailable.
//   5. INPUT GUARD: negative GPU index → Fail before any native call.
//   6. FAN = HONEST UNAVAILABLE (§22.3): GetFanCurve always fails clean — the NVAPI
//      fan-curve read is not in the verified recipe; never a fake/empty curve.
// -----------------------------------------------------------------------
public sealed class NvApiGpuTuningReaderTests
{
    // ── IsAvailable ──────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_is_false_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_is_true_when_native_available()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        sut.IsAvailable.Should().BeTrue();
    }

    // ── GetCoreClockOffsetMhz ─────────────────────────────────────────────

    [Fact]
    public void GetCoreClockOffsetMhz_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetCoreClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetCoreClockOffsetMhz_returns_ok_with_value_when_read_succeeds()
    {
        var fake = new FakeNvApiNative
        {
            IsAvailable   = true,
            CoreSuccess   = true,
            CoreOffsetMhz = -15,
        };
        var r = new NvApiGpuTuningReader(fake).GetCoreClockOffsetMhz(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(-15);
    }

    [Fact]
    public void GetCoreClockOffsetMhz_returns_fail_when_read_fails()
    {
        // The fake carries a non-zero value but the read FAILS → the reader must Fail,
        // never leak the stale value as a fake number.
        var fake = new FakeNvApiNative { IsAvailable = true, CoreSuccess = false, CoreOffsetMhz = 123 };
        var r    = new NvApiGpuTuningReader(fake).GetCoreClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetCoreClockOffsetMhz_returns_fail_for_negative_gpu_index()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetCoreClockOffsetMhz(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── GetMemoryClockOffsetMhz (real read — not fail-clean by design) ────

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetMemoryClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_ok_with_REAL_value_when_read_succeeds()
    {
        // The memory offset is a real read off the native boundary — not a hardcoded 0.
        var fake = new FakeNvApiNative
        {
            IsAvailable     = true,
            MemorySuccess   = true,
            MemoryOffsetMhz = 200,
        };
        var r = new NvApiGpuTuningReader(fake).GetMemoryClockOffsetMhz(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(200);
    }

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_fail_when_read_fails()
    {
        // Memory clock domain genuinely absent on this GPU → native returns false →
        // the reader fails clean honestly (never a fabricated 0).
        var fake = new FakeNvApiNative { IsAvailable = true, MemorySuccess = false, MemoryOffsetMhz = 999 };
        var r    = new NvApiGpuTuningReader(fake).GetMemoryClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_fail_for_negative_gpu_index()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetMemoryClockOffsetMhz(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void Core_and_memory_offsets_are_independent_real_reads()
    {
        // Pins the design correction: core and memory are SEPARATE real reads, each
        // mapping its own native value — proving memory is not aliased to core / 0.
        var fake = new FakeNvApiNative
        {
            IsAvailable     = true,
            CoreOffsetMhz   = -30,
            MemoryOffsetMhz = 500,
        };
        var sut = new NvApiGpuTuningReader(fake);
        sut.GetCoreClockOffsetMhz(0).Value.Should().Be(-30);
        sut.GetMemoryClockOffsetMhz(0).Value.Should().Be(500);
    }

    // ── GetPowerLimitPct ──────────────────────────────────────────────────

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_ok_with_pct_when_read_succeeds()
    {
        var fake = new FakeNvApiNative
        {
            IsAvailable   = true,
            PowerSuccess  = true,
            PowerLimitPct = 90,
        };
        var r = new NvApiGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(90);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_read_fails()
    {
        var fake = new FakeNvApiNative { IsAvailable = true, PowerSuccess = false, PowerLimitPct = 77 };
        var r    = new NvApiGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_for_negative_gpu_index()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetPowerLimitPct(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── GetFanCurve — HONEST UNAVAILABLE (§22.3), never a fake curve ──────

    [Fact]
    public void GetFanCurve_returns_honest_unavailable_even_when_native_available()
    {
        // The NVAPI fan-curve read is not in the verified recipe and the fan tweak is
        // deferred → honest fail-clean, never an empty/fabricated curve.
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetFanCurve_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── Native unavailable never throws ───────────────────────────────────

    [Fact]
    public void All_reads_on_unavailable_native_never_throw()
    {
        var sut  = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var act1 = () => sut.GetCoreClockOffsetMhz(0);
        var act2 = () => sut.GetMemoryClockOffsetMhz(0);
        var act3 = () => sut.GetPowerLimitPct(0);
        var act4 = () => sut.GetFanCurve(0);
        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();
        act4.Should().NotThrow();
    }
}

// ────────────────────────────────────────────────────────────────────────
// FakeNvApiNative — in-process substitute for unit tests.
// Never calls nvapi64.dll; fully configurable per test. The Try* reads return the
// configured value + a per-surface success flag; the Try*Set writes are never reached
// by the read seam (and return false here — no driver is touched in tests).
// ────────────────────────────────────────────────────────────────────────
internal sealed class FakeNvApiNative : INvApiNative
{
    public bool IsAvailable { get; set; }

    // ── read state ────────────────────────────────────────────────────────
    public bool CoreSuccess   { get; set; } = true;
    public int  CoreOffsetMhz { get; set; }

    public bool MemorySuccess   { get; set; } = true;
    public int  MemoryOffsetMhz { get; set; }

    public bool PowerSuccess  { get; set; } = true;
    public int  PowerLimitPct { get; set; }

    public bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        offsetMhz = CoreOffsetMhz;
        return CoreSuccess;
    }

    public bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        offsetMhz = MemoryOffsetMhz;
        return MemorySuccess;
    }

    public bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct)
    {
        powerLimitPct = PowerLimitPct;
        return PowerSuccess;
    }

    // ── write surface — never exercised by the non-elevated read reader ────
    public bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz) => false;
    public bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz) => false;
    public bool TrySetPowerLimitPercent(int gpuIndex, float pct) => false;
    public bool TrySetDrsGlobalSetting(uint settingId, uint value) => false;
    // Cette doublure sert le seam de lecture : aucune ecriture ne touche un driver dans les tests.
    public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value) => false;

    // ── DRS read (non-elevated) ────────────────────────────────────────────
    public bool DrsReadSuccess { get; set; }
    public uint DrsSettingValue { get; set; }
    public uint DrsSettingLocation { get; set; }
    public uint DrsIsCurrentPredefined { get; set; }
    public uint DrsIsPredefinedValid { get; set; }

    public bool TryGetDrsGlobalSetting(uint settingId, out uint value)
    {
        value = DrsSettingValue;
        return DrsReadSuccess;
    }

    public bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading)
    {
        reading = new DrsReading(DrsSettingValue, DrsSettingLocation,
            DrsIsCurrentPredefined, DrsIsPredefinedValid);
        return DrsReadSuccess;
    }

    // ── DRS read, profil par application ──────────────────────────────────
    public bool AppDrsReadSuccess { get; set; }
    public uint AppDrsSettingValue { get; set; }
    public uint AppDrsSettingLocation { get; set; }
    public uint AppDrsIsCurrentPredefined { get; set; }
    public int AppDrsCalls { get; private set; }

    public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
    {
        AppDrsCalls++;
        reading = new DrsReading(AppDrsSettingValue, AppDrsSettingLocation, AppDrsIsCurrentPredefined, 0);
        return AppDrsReadSuccess;
    }

    /// <summary>Echec au jeu parce que l'executable n'a aucun profil (EXECUTABLE_NOT_FOUND).</summary>
    public bool AppDrsPasDeProfil { get; set; }

    public INvApiNative.LectureAuJeu LireDrsAuJeu(string applicationPath, uint settingId, out DrsReading reading)
        => TryReadDrsSettingForApplication(applicationPath, settingId, out reading)
            ? INvApiNative.LectureAuJeu.Lu
            : AppDrsPasDeProfil ? INvApiNative.LectureAuJeu.PasDeProfil : INvApiNative.LectureAuJeu.Illisible;
}
