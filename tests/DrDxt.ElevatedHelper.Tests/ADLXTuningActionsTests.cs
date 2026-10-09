using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.ElevatedHelper.Execution.Adlx;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// T2 — ADLXTuningActions: the RISKY AMD ADLX tuning write seam (AMD mirror of
// NvApiTuningActions). Everything here is provable without a GPU because the native
// boundary (IAdlxNative) is replaced by FakeAdlxNative — a unit test must never write a
// real clock/power delta to live hardware, so the real SetTuning path is exercised only
// against the fake (the real ADLX write is validated separately by the live harness).
//
// What we pin (the helper's contract, derived from the spec + the NVAPI precedent):
//   1. GET-then-SET ordering — Undervolt reads the current GFX state before it writes.
//   2. MaxFreq-only / voltage-unchanged — Undervolt writes the absolute MaxFreq target it is
//      given VERBATIM (the App already turned the user's offset into an absolute target) and
//      passes the CURRENT voltage straight back (so the native boundary skips the voltage
//      write — RDNA4-safe).
//   3. PowerLimit routes to TrySetPowerLimit with the exact percent (and touches no GFX).
//   4. DRIVER-RANGE defense-in-depth — an absolute target OUTSIDE the driver's own reported
//      MaxFreq range is REJECTED at the helper (reject, never clamp) and never reaches the
//      driver write. (The conservative OFFSET policy band lives at the App layer; PowerLimit's
//      % band is still mirrored + enforced here.)
//   5. FAIL-CLEAN — unavailable ADLX / read fault / write fault / unsupported kind / bad
//      index all return Result.Fail(HELPER.OP_FAILED) and never a false Ok, never throw.
// -----------------------------------------------------------------------

/// <summary>A controllable fake of the native ADLX boundary. Records the ORDER and the
/// ARGUMENTS of every native call so the helper's GET-then-SET contract, MaxFreq-only write
/// and default-DENY fail-clean behaviour can be proven without a GPU. Never touches native
/// code.</summary>
internal sealed class FakeAdlxNative : IAdlxNative
{
    public bool IsAvailable { get; set; } = true;

    // Configurable GET result (the current GFX state the helper reads back).
    public bool ReadGfxSucceeds { get; set; } = true;
    public AdlxGfxReads GfxReads { get; set; }

    // Configurable SET results.
    public bool SetGfxSucceeds { get; set; } = true;
    public bool SetPowerSucceeds { get; set; } = true;

    // Fault injection: make any native call throw, to prove the helper never lets an
    // exception cross the IPC (it must be caught and turned into a clean Result.Fail).
    public bool ThrowOnAnyCall { get; set; }

    // Recorded native interaction (ordering + captured write arguments).
    public List<string> Calls { get; } = new();
    public int? SetGfxMaxFreqMhz { get; private set; }
    public int? SetGfxVoltMv { get; private set; }
    public int? SetPowerPct { get; private set; }

    public bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads)
    {
        Calls.Add(nameof(TryReadGfx));
        if (ThrowOnAnyCall) throw new InvalidOperationException("simulated native fault");
        reads = GfxReads;
        return ReadGfxSucceeds;
    }

    public bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original)
    {
        Calls.Add(nameof(TrySetGfx));
        SetGfxMaxFreqMhz = newMaxFreqMhz;
        SetGfxVoltMv = newVoltMv;
        original = GfxReads;
        if (ThrowOnAnyCall) throw new InvalidOperationException("simulated native fault");
        return SetGfxSucceeds;
    }

    public bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original)
    {
        Calls.Add(nameof(TrySetPowerLimit));
        SetPowerPct = newPowerLimitPct;
        original = default;
        if (ThrowOnAnyCall) throw new InvalidOperationException("simulated native fault");
        return SetPowerSucceeds;
    }

    // Read-only members not used by the write seam — fail clean (and never logged as a write).
    public bool TryReadPower(int gpuIndex, out AdlxPowerReads reads) { reads = default; return false; }
    public bool TryReadFan(int gpuIndex, out AdlxFanReads reads) { reads = default; return false; }
    public IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex) => Array.Empty<GpuProbeStep>();

    public string? AntiLagRefus { get; set; }
    public bool? AntiLagEcrit { get; private set; }

    public int? FrtcEcrit { get; private set; }
    public int? VsyncEcrit { get; private set; }

    public string? EcrireVsync(int mode)
    {
        Calls.Add(nameof(EcrireVsync));
        VsyncEcrit = mode;
        return null;
    }

    public string? EcrireFrtc(int code)
    {
        Calls.Add(nameof(EcrireFrtc));
        FrtcEcrit = code;
        return null;
    }

    public string? EcrireAntiLag(bool actif)
    {
        Calls.Add(nameof(EcrireAntiLag));
        if (AntiLagRefus is null) AntiLagEcrit = actif;
        return AntiLagRefus;
    }
}

public sealed class ADLXTuningActionsTests
{
    // A current GFX state with generous ranges so any in-band offset is itself in range.
    private static AdlxGfxReads StockGfx(int maxFreqMhz = 2900, int voltMv = 1100) => new()
    {
        MaxFreqMhz = maxFreqMhz,
        MaxFreqRange = new AdlxIntRange { Min = 500, Max = 4000, Step = 1 },
        VoltMv = voltMv,
        VoltRange = new AdlxIntRange { Min = 0, Max = 1200, Step = 1 },
        MinFreqMhz = 500,
        MinFreqRange = new AdlxIntRange { Min = 500, Max = 4000, Step = 1 },
    };

    // --- 1+2: GET-then-SET, MaxFreq-only, voltage unchanged --------------------------------

    [Fact]
    public void Undervolt_reads_then_writes_the_absolute_target_maxfreq_only_with_voltage_unchanged()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2900, voltMv: 1100) };
        var actions = new ADLXTuningActions(fake);

        // The op now carries an absolute target MaxFreq (the App already turned the user's
        // offset into an absolute target by reading the current MaxFreq).
        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800);

        result.IsOk.Should().BeTrue("an in-range absolute target with a healthy GPU must apply");
        // GET-then-SET ordering: the current state is read before anything is written.
        fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx), nameof(IAdlxNative.TrySetGfx));
        // The absolute target is written VERBATIM — no +offset arithmetic at the helper.
        fake.SetGfxMaxFreqMhz.Should().Be(2800);
        // Voltage is passed back UNCHANGED — proves MaxFreq-only (the native boundary then
        // skips the no-op voltage write, which is what keeps RDNA4 working).
        fake.SetGfxVoltMv.Should().Be(1100);
    }

    // --- 3: PowerLimit routes to TrySetPowerLimit, touches no GFX ---------------------------

    [Fact]
    public void PowerLimit_routes_to_TrySetPowerLimit_with_the_exact_percent_and_no_gfx_write()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.PowerLimit, 80);

        result.IsOk.Should().BeTrue();
        fake.Calls.Should().Equal(nameof(IAdlxNative.TrySetPowerLimit));
        fake.SetPowerPct.Should().Be(80);
        fake.SetGfxMaxFreqMhz.Should().BeNull("a power-limit write must never touch GFX tuning");
    }

    // --- 4: driver-range defense-in-depth — an absolute target outside the DRIVER's reported
    //        MaxFreq range is REJECTED, never reaches the driver write -----------------------
    // StockGfx reports a hardware MaxFreq range of [500, 4000] MHz. The helper reads the GFX
    // state first (the range comes from that read), then refuses any absolute target outside
    // it — so the GET happens on a rejected target, but the write never does.

    [Theory]
    [InlineData(2800, true)]            // inside the driver range — reaches the write
    [InlineData(500, true)]             // lower hardware edge — reaches the write
    [InlineData(4000, true)]            // upper hardware edge — reaches the write
    [InlineData(499, false)]            // one below the driver floor — rejected
    [InlineData(4001, false)]           // one above the driver ceiling — rejected
    [InlineData(int.MinValue, false)]   // total, no overflow
    [InlineData(int.MaxValue, false)]
    public void Undervolt_rejects_a_target_outside_the_driver_hardware_range(int targetMhz, bool reachesDriver)
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx() }; // range [500, 4000]
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, targetMhz);

        if (reachesDriver)
        {
            result.IsOk.Should().BeTrue();
            fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx), nameof(IAdlxNative.TrySetGfx));
            fake.SetGfxMaxFreqMhz.Should().Be(targetMhz, "an in-range target is written verbatim");
        }
        else
        {
            result.IsOk.Should().BeFalse();
            result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
            // The GET happened (the range is read from it) but the write never did.
            fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx));
            fake.SetGfxMaxFreqMhz.Should().BeNull("an out-of-range target must NEVER reach the driver write");
        }
    }

    [Theory]
    [InlineData(60, true)]              // floor — reaches the driver
    [InlineData(100, true)]             // ceiling — reaches the driver
    [InlineData(80, true)]
    [InlineData(59, false)]             // below the floor — rejected
    [InlineData(101, false)]            // above the ceiling — rejected
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void PowerLimit_enforces_the_safe_bounds_band(int pct, bool reachesDriver)
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.PowerLimit, pct);

        if (reachesDriver)
        {
            result.IsOk.Should().BeTrue();
            fake.Calls.Should().Equal(nameof(IAdlxNative.TrySetPowerLimit));
            fake.SetPowerPct.Should().Be(pct);
        }
        else
        {
            result.IsOk.Should().BeFalse();
            result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
            fake.Calls.Should().BeEmpty("an out-of-band power limit must NEVER touch the driver");
        }
    }

    // --- 5: fail-clean — never a false Ok, never a throw -----------------------------------

    [Fact]
    public void Unavailable_adlx_fails_clean_and_writes_nothing()
    {
        var fake = new FakeAdlxNative { IsAvailable = false, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var undervolt = actions.SetTuning(0, AdlxTuningKind.Undervolt, -50);
        var power = actions.SetTuning(0, AdlxTuningKind.PowerLimit, 80);

        undervolt.IsOk.Should().BeFalse();
        undervolt.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        power.IsOk.Should().BeFalse();
        power.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().BeEmpty("no native call may be made when ADLX is unavailable");
    }

    [Fact]
    public void Negative_gpu_index_fails_clean_and_writes_nothing()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(-1, AdlxTuningKind.Undervolt, -50);

        result.IsOk.Should().BeFalse("a negative GPU index is never valid");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Gfx_read_failure_fails_clean_and_never_writes()
    {
        var fake = new FakeAdlxNative { ReadGfxSucceeds = false, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800);

        result.IsOk.Should().BeFalse("a failed pre-write read must abort before any write");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx));
    }

    [Fact]
    public void Gfx_write_failure_fails_clean_and_never_reports_ok()
    {
        var fake = new FakeAdlxNative { SetGfxSucceeds = false, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        // An IN-RANGE absolute target so the path reaches the write (whose failure is the point).
        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800);

        result.IsOk.Should().BeFalse("a driver write failure must NEVER surface as Ok");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx), nameof(IAdlxNative.TrySetGfx));
    }

    [Fact]
    public void Power_write_failure_fails_clean_and_never_reports_ok()
    {
        var fake = new FakeAdlxNative { SetPowerSucceeds = false };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.PowerLimit, 80);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().Equal(nameof(IAdlxNative.TrySetPowerLimit));
    }

    // A2 (07/10/2026) : Anti-Lag s'écrit (0 ou 1) par la couture native ; son refus remonte, une autre valeur n'y va pas.
    [Fact]
    public void AntiLag_writes_through_the_native_seam_and_its_refusal_surfaces()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        actions.SetTuning(0, AdlxTuningKind.AntiLag, 1).IsOk.Should().BeTrue();
        fake.AntiLagEcrit.Should().BeTrue();
        actions.SetTuning(0, AdlxTuningKind.AntiLag, 0).IsOk.Should().BeTrue();
        fake.AntiLagEcrit.Should().BeFalse();

        fake.AntiLagRefus = "Anti-Lag n'est pas pris en charge par cette carte.";
        var refus = actions.SetTuning(0, AdlxTuningKind.AntiLag, 1);
        refus.IsOk.Should().BeFalse();
        refus.Error!.Value.Message.Should().Contain("pas pris en charge");
    }

    // A2b : FRTC passe son code tel quel (cible ou moins la cible) ; 0 n'atteint pas le pilote.
    [Fact]
    public void FrameRateTarget_passes_its_code_and_zero_never_reaches_the_driver()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        actions.SetTuning(0, AdlxTuningKind.FrameRateTarget, 141).IsOk.Should().BeTrue();
        fake.FrtcEcrit.Should().Be(141);
        actions.SetTuning(0, AdlxTuningKind.FrameRateTarget, -60).IsOk.Should().BeTrue();
        fake.FrtcEcrit.Should().Be(-60);

        var zero = new FakeAdlxNative();
        new ADLXTuningActions(zero).SetTuning(0, AdlxTuningKind.FrameRateTarget, 0).IsOk.Should().BeFalse();
        zero.Calls.Should().BeEmpty();
    }

    // A2c : la Vsync du pilote AMD, mode 0 à 3 ; un autre mode n'atteint pas le pilote.
    [Fact]
    public void VerticalRefresh_passes_modes_0_to_3_and_refuses_the_rest()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        actions.SetTuning(0, AdlxTuningKind.VerticalRefresh, 3).IsOk.Should().BeTrue();
        fake.VsyncEcrit.Should().Be(3);
        actions.SetTuning(0, AdlxTuningKind.VerticalRefresh, 0).IsOk.Should().BeTrue();
        fake.VsyncEcrit.Should().Be(0);

        var hors = new FakeAdlxNative();
        new ADLXTuningActions(hors).SetTuning(0, AdlxTuningKind.VerticalRefresh, 4).IsOk.Should().BeFalse();
        hors.Calls.Should().BeEmpty();
    }

    [Fact]
    public void AntiLag_value_other_than_0_or_1_is_refused_before_the_driver()
    {
        var fake = new FakeAdlxNative();

        new ADLXTuningActions(fake).SetTuning(0, AdlxTuningKind.AntiLag, 2).IsOk.Should().BeFalse();
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Undefined_kind_fails_clean_and_writes_nothing()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, (AdlxTuningKind)99, 0);

        result.IsOk.Should().BeFalse("an undefined enum value must be refused, never a crash");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Never_throws_even_when_the_native_boundary_faults()
    {
        var fake = new FakeAdlxNative { ThrowOnAnyCall = true, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var act = () => actions.SetTuning(0, AdlxTuningKind.Undervolt, -50);

        var result = act.Should().NotThrow(
            "an exception must NEVER cross the IPC — it is caught and returned as a clean Result").Subject;
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public void Public_constructor_wires_the_real_native_boundary_without_throwing()
    {
        // The parameterless ctor wires the real AdlxNative (lazy init — no ADLX call here),
        // so constructing the seam is always safe even on a non-AMD box.
        var act = () => new ADLXTuningActions();

        act.Should().NotThrow();
    }
}
