using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// R2 (RISKY half) — NvApiTuningActions: what we CAN prove without a GPU.
//
// The native NVAPI boundary (the reverse-engineered SetPstates20 /
// ClientPowerPoliciesSetStatus recipe, the interop struct sizes, the P0-clock finder,
// and the MHz/% unit conversions) now lives in DrDxt.GpuNative (NvApiNative) and is
// pinned by DrDxt.GpuNative.Tests. What remains testable here is the helper-side
// orchestrator + policy:
//
//   1. Fail-clean contract — the read-only Get* methods never throw and return a
//      Result; on a box without NVAPI they fail clean (HELPER.OP_FAILED). Safe to run
//      on a real NVIDIA box (read-only).
//   2. POLICY RAILS — the conservative offset / power bands (NvApiTuningMath) the
//      elevated write re-checks before it ever reaches the native boundary.
//
// We deliberately DO not call SetCoreClockOffset / SetPowerLimitPercent here — a unit
// test must never write a clock/power delta to a real GPU.
// -----------------------------------------------------------------------
public sealed class NvApiTuningActionsContractTests
{
    [Fact]
    public void GetCoreClockOffsetMhz_never_throws_and_returns_a_result()
    {
        var tuning = new NvApiTuningActions();

        var act = () => tuning.GetCoreClockOffsetMhz(0);

        var result = act.Should().NotThrow(
            "the NVAPI read boundary must fail-clean, never throw across the IPC").Subject;
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetPowerLimitPercent_never_throws_and_returns_a_result()
    {
        var tuning = new NvApiTuningActions();

        var act = () => tuning.GetPowerLimitPercent(0);

        var result = act.Should().NotThrow().Subject;
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetCoreClockOffsetMhz_on_a_box_without_nvapi_fails_clean()
    {
        // CI/dev runners (no NVIDIA): expected path is a clean HELPER.OP_FAILED, never
        // a throw. Guarded so it stays correct on a machine that DOES have an NVIDIA
        // driver (there the read may legitimately succeed — we only pin the no-NVIDIA
        // fail-clean shape).
        var result = new NvApiTuningActions().GetCoreClockOffsetMhz(0);

        if (!result.IsOk)
        {
            result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
            result.Error!.Value.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetCoreClockOffsetMhz_rejects_a_negative_gpu_index_clean()
    {
        var result = new NvApiTuningActions().GetCoreClockOffsetMhz(-1);

        result.IsOk.Should().BeFalse("a negative GPU index is never valid");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }
}

public sealed class NvApiTuningRailTests
{
    // The hard safety rails are pure and must never throw — int.MinValue (no Math.Abs!)
    // and NaN (all NaN comparisons are false) are the traps the red-team flagged.
    [Fact]
    public void Core_offset_rail_accepts_a_normal_undervolt_offset() =>
        NvApiTuningMath.IsCoreOffsetWithinHardRail(-120).Should().BeTrue();

    [Fact]
    public void Core_offset_rail_rejects_int_min_value_without_throwing()
    {
        var act = () => NvApiTuningMath.IsCoreOffsetWithinHardRail(int.MinValue);

        act.Should().NotThrow("the rail must never overflow on int.MinValue")
           .Subject.Should().BeFalse();
    }

    [Fact]
    public void Core_offset_rail_rejects_an_absurd_offset() =>
        NvApiTuningMath.IsCoreOffsetWithinHardRail(5000).Should().BeFalse();

    [Fact]
    public void Power_rail_accepts_a_normal_percent() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(90).Should().BeTrue();

    [Fact]
    public void Power_rail_rejects_nan() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(double.NaN).Should().BeFalse();

    [Fact]
    public void Power_rail_rejects_infinity() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(double.PositiveInfinity).Should().BeFalse();

    [Fact]
    public void Power_rail_rejects_an_absurd_percent() =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(500).Should().BeFalse();

    // 26/09/2026 : la borne BASSE n'etait tenue par aucun test. C'est elle qui borne ce qu'un op eleve peut
    // faire de la limite de puissance (l'allowlist accepte 0 a 100 %) : ramenee a 0, rien ne l'aurait vu.
    [Theory]
    [InlineData(49.9, false)]
    [InlineData(50.0, true)]
    [InlineData(120.0, true)]
    [InlineData(120.1, false)]
    public void Power_rail_bounds_are_exact(double percent, bool accepted) =>
        NvApiTuningMath.IsPowerPercentWithinHardRail(percent).Should().Be(accepted);
}
