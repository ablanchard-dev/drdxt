using DrDxt.Application.Tweaks.Gpu;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// M2 Phase E1 — GpuSafeBounds.
//
// The conservative bounds gate that sits in front of the elevated helper: no
// value outside these bounds can ever reach the driver. Values are deliberately
// TIMID for M2 (hardened later with team data §10). Pure, fully unit-tested.
//
//  - Core clock offset is bounded to a conservative band (undervolt = a small
//    negative core-clock offset under Option A; an over-aggressive offset is
//    refused outright before it can touch the driver).
//  - Power limit can never go below a safe floor (starving the GPU = stutter).
//  - Fan curve can never drop below a minimum duty at high temperature
//    (anti-overheat) — a "0 RPM everywhere" curve is clamped up.
// -----------------------------------------------------------------------
public sealed class GpuSafeBoundsTests
{
    // --- Core clock offset band (MHz, signed; negative = undervolt direction). ---

    [Theory]
    [InlineData(-150, true)]   // conservative undervolt offset → OK
    [InlineData(0, true)]      // no-op offset → OK
    [InlineData(30, true)]     // small positive offset within band → OK
    [InlineData(-400, false)]  // too aggressive (undervolt) → refused outright
    [InlineData(500, false)]   // too aggressive (overclock) → refused outright
    public void Core_offset_must_stay_within_conservative_band(int mhz, bool ok) =>
        GpuSafeBounds.IsCoreOffsetSafe(mhz).Should().Be(ok);

    [Fact]
    public void Core_offset_band_is_exactly_the_published_min_max()
    {
        // The band edges are inclusive and conservative.
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MinCoreOffsetMhz).Should().BeTrue();
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MaxCoreOffsetMhz).Should().BeTrue();
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MinCoreOffsetMhz - 1).Should().BeFalse();
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MaxCoreOffsetMhz + 1).Should().BeFalse();
    }

    // --- Memory offset band. ---

    [Theory]
    [InlineData(0, true)]
    [InlineData(200, true)]
    [InlineData(2000, false)]
    [InlineData(-2000, false)]
    public void Memory_offset_must_stay_within_conservative_band(int mhz, bool ok) =>
        GpuSafeBounds.IsMemoryOffsetSafe(mhz).Should().Be(ok);

    // --- Power-limit floor (% of TDP). ---

    [Fact]
    public void Power_limit_floor_never_below_safe_minimum() =>
        GpuSafeBounds.ClampPowerLimitPct(40).Should().BeGreaterThanOrEqualTo(GpuSafeBounds.MinPowerLimitPct);

    [Fact]
    public void Power_limit_clamp_raises_below_floor_to_the_floor() =>
        GpuSafeBounds.ClampPowerLimitPct(10).Should().Be(GpuSafeBounds.MinPowerLimitPct);

    [Fact]
    public void Power_limit_clamp_leaves_in_range_value_untouched() =>
        GpuSafeBounds.ClampPowerLimitPct(85).Should().Be(85);

    [Fact]
    public void Power_limit_clamp_caps_above_100_at_100() =>
        GpuSafeBounds.ClampPowerLimitPct(140).Should().Be(100);

    [Fact]
    public void Power_limit_floor_is_meaningfully_conservative() =>
        // Never let the GPU be starved below half power (would cause stutter, not gains).
        GpuSafeBounds.MinPowerLimitPct.Should().BeGreaterThanOrEqualTo(50);

    [Fact]
    public void Is_power_limit_safe_rejects_values_below_the_floor()
    {
        GpuSafeBounds.IsPowerLimitSafe(GpuSafeBounds.MinPowerLimitPct).Should().BeTrue();
        GpuSafeBounds.IsPowerLimitSafe(GpuSafeBounds.MinPowerLimitPct - 1).Should().BeFalse();
        GpuSafeBounds.IsPowerLimitSafe(100).Should().BeTrue();
        GpuSafeBounds.IsPowerLimitSafe(101).Should().BeFalse();
    }

    // --- Fan curve: min duty at high temperature (anti-overheat). ---

    [Fact]
    public void Fan_curve_min_duty_prevents_overheat()
    {
        var zeroRpmEverywhere = new FanCurve(new[]
        {
            new FanCurvePoint(TempC: 40, DutyPct: 0),
            new FanCurvePoint(TempC: 70, DutyPct: 0),
            new FanCurvePoint(TempC: 85, DutyPct: 0),  // hot, but silent = dangerous
        });

        var clamped = GpuSafeBounds.ClampFanCurve(zeroRpmEverywhere);

        clamped.MinDutyAtHotTemp.Should().BeGreaterThanOrEqualTo(GpuSafeBounds.MinFanDutyAtHotTempPct);
        GpuSafeBounds.MinFanDutyAtHotTempPct.Should().BeGreaterThanOrEqualTo(30);
    }

    [Fact]
    public void Fan_curve_clamp_only_raises_hot_points_below_the_floor()
    {
        var curve = new FanCurve(new[]
        {
            new FanCurvePoint(TempC: 40, DutyPct: 20),  // cool: low duty is fine, left alone
            new FanCurvePoint(TempC: 85, DutyPct: 10),  // hot: below floor → raised
        });

        var clamped = GpuSafeBounds.ClampFanCurve(curve);

        // The cool point keeps its quiet duty.
        clamped.Points.First(p => p.TempC == 40).DutyPct.Should().Be(20);
        // The hot point is raised to at least the floor.
        clamped.Points.First(p => p.TempC == 85).DutyPct.Should().BeGreaterThanOrEqualTo(GpuSafeBounds.MinFanDutyAtHotTempPct);
    }

    [Fact]
    public void Fan_curve_clamp_leaves_a_safe_aggressive_curve_untouched()
    {
        var safe = new FanCurve(new[]
        {
            new FanCurvePoint(TempC: 40, DutyPct: 30),
            new FanCurvePoint(TempC: 85, DutyPct: 80),
        });

        var clamped = GpuSafeBounds.ClampFanCurve(safe);

        clamped.Points.Should().BeEquivalentTo(safe.Points);
    }

    [Fact]
    public void Is_fan_curve_safe_flags_a_hot_silent_curve()
    {
        var hotSilent = new FanCurve(new[] { new FanCurvePoint(TempC: 85, DutyPct: 5) });
        var safe = new FanCurve(new[] { new FanCurvePoint(TempC: 85, DutyPct: 60) });

        GpuSafeBounds.IsFanCurveSafe(hotSilent).Should().BeFalse();
        GpuSafeBounds.IsFanCurveSafe(safe).Should().BeTrue();
    }

    /// <summary>24/09/2026 : la promesse porte sur la température, pas sur les points. Le point qui
    /// gouverne 80 °C tient le plancher, sinon le ventilateur descend sous 40 % juste au-dessus.</summary>
    [Fact]
    public void Is_fan_curve_safe_regarde_le_point_qui_gouverne_80C()
    {
        GpuSafeBounds.IsFanCurveSafe(new FanCurve(new[] { new FanCurvePoint(79, 10), new FanCurvePoint(100, 40) }))
            .Should().BeFalse("à 85 °C : 10 % tenu, ~17 % interpolé");
        GpuSafeBounds.IsFanCurveSafe(new FanCurve(new[] { new FanCurvePoint(60, 20) }))
            .Should().BeFalse("un seul point froid : 20 % tenu à 90 °C");
        GpuSafeBounds.IsFanCurveSafe(new FanCurve(new[]
            { new FanCurvePoint(40, 30), new FanCurvePoint(60, 45), new FanCurvePoint(80, 65), new FanCurvePoint(90, 85) }))
            .Should().BeTrue("la courbe livrée par le produit");
    }
}
