using DrDxt.Application.Tweaks.Gpu;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuSafeBounds.
//
// La porte de bornes prudentes devant le helper élevé : aucune valeur hors de ces bornes
// ne peut atteindre le pilote. Les valeurs sont timides à dessein (à durcir avec des
// données de terrain). Pur, entièrement testé.
//
//  - Le décalage d'horloge cœur est borné à une bande prudente (undervolt = petit décalage
//    négatif de l'horloge cœur ; un décalage trop agressif est refusé net avant de
//    pouvoir toucher le pilote).
//  - La limite de puissance ne descend jamais sous un plancher sûr (affamer la carte = saccades).
//  - La courbe de ventilation ne descend jamais sous un régime minimal à haute température
//    (anti-surchauffe) : une courbe « 0 tr/min partout » est relevée.
// -----------------------------------------------------------------------
public sealed class GpuSafeBoundsTests
{
    // --- Bande de décalage d'horloge cœur (MHz, signé ; négatif = sens undervolt). ---

    [Theory]
    [InlineData(-150, true)]   // décalage d'undervolt prudent → OK
    [InlineData(0, true)]      // no-op offset → OK
    [InlineData(30, true)]     // petit décalage positif dans la bande → OK
    [InlineData(-400, false)]  // trop agressif (undervolt) → refusé net
    [InlineData(500, false)]   // trop agressif (overclock) → refusé net
    public void Core_offset_must_stay_within_conservative_band(int mhz, bool ok) =>
        GpuSafeBounds.IsCoreOffsetSafe(mhz).Should().Be(ok);

    [Fact]
    public void Core_offset_band_is_exactly_the_published_min_max()
    {
        // Les bords de la bande sont inclus et prudents.
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MinCoreOffsetMhz).Should().BeTrue();
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MaxCoreOffsetMhz).Should().BeTrue();
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MinCoreOffsetMhz - 1).Should().BeFalse();
        GpuSafeBounds.IsCoreOffsetSafe(GpuSafeBounds.MaxCoreOffsetMhz + 1).Should().BeFalse();
    }

    // --- Bande de décalage mémoire. ---

    [Theory]
    [InlineData(0, true)]
    [InlineData(200, true)]
    [InlineData(2000, false)]
    [InlineData(-2000, false)]
    public void Memory_offset_must_stay_within_conservative_band(int mhz, bool ok) =>
        GpuSafeBounds.IsMemoryOffsetSafe(mhz).Should().Be(ok);

    // --- Plancher de limite de puissance (% du TDP). ---

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
        // Ne jamais affamer la carte sous la moitié de sa puissance (saccades, pas de gain).
        GpuSafeBounds.MinPowerLimitPct.Should().BeGreaterThanOrEqualTo(50);

    [Fact]
    public void Is_power_limit_safe_rejects_values_below_the_floor()
    {
        GpuSafeBounds.IsPowerLimitSafe(GpuSafeBounds.MinPowerLimitPct).Should().BeTrue();
        GpuSafeBounds.IsPowerLimitSafe(GpuSafeBounds.MinPowerLimitPct - 1).Should().BeFalse();
        GpuSafeBounds.IsPowerLimitSafe(100).Should().BeTrue();
        GpuSafeBounds.IsPowerLimitSafe(101).Should().BeFalse();
    }

    // --- Courbe de ventilation : régime minimal à haute température (anti-surchauffe). ---

    [Fact]
    public void Fan_curve_min_duty_prevents_overheat()
    {
        var zeroRpmEverywhere = new FanCurve(new[]
        {
            new FanCurvePoint(TempC: 40, DutyPct: 0),
            new FanCurvePoint(TempC: 70, DutyPct: 0),
            new FanCurvePoint(TempC: 85, DutyPct: 0),  // chaud mais silencieux = dangereux
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
            new FanCurvePoint(TempC: 40, DutyPct: 20),  // froid : un régime bas convient, laissé tel quel
            new FanCurvePoint(TempC: 85, DutyPct: 10),  // chaud : sous le plancher → relevé
        });

        var clamped = GpuSafeBounds.ClampFanCurve(curve);

        // Le point froid garde son régime silencieux.
        clamped.Points.First(p => p.TempC == 40).DutyPct.Should().Be(20);
        // Le point chaud est relevé au moins au plancher.
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

    /// <summary>La promesse porte sur la température, pas sur les points. Le point qui
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
