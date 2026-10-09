using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Guard: the M2 Phase E GPU-tuning tweaks may only apply ops the allowlist permits.
//
// These reconstruct the exact typed ops the E2/E3/E4 tweaks emit and assert
// OpAllowlist accepts the in-bounds ones and DENIES the dangerous ones. If a tweak
// ever drifts to a non-allowlisted op, the corresponding arm here is the canary —
// change the allowlist intentionally (leaf-exact), never silently.
// -----------------------------------------------------------------------
public sealed class GpuTweakOpsAllowlistTests
{
    private readonly OpAllowlist _allow = new();

    // --- E2 undervolt: clock offset (NVIDIA) / undervolt tuning (AMD). ---

    [Fact]
    public void E2_nvapi_clock_offset_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, CoreOffsetMhz: -120, MemoryOffsetMhz: 0))
            .IsOk.Should().BeTrue();

    [Fact]
    public void E2_adlx_undervolt_is_allowlisted() =>
        _allow.Validate(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, IntValue: -100))
            .IsOk.Should().BeTrue();

    [Fact]
    public void E2_negative_gpu_index_is_denied() =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(-1, 0, 0))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- E3 power limit: NVIDIA / AMD. ---

    [Fact]
    public void E3_nvapi_power_limit_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiPowerLimitOp(0, PowerLimitPercent: 80))
            .IsOk.Should().BeTrue();

    [Fact]
    public void E3_adlx_power_limit_is_allowlisted() =>
        _allow.Validate(new ApplyAdlxTuningOp(0, AdlxTuningKind.PowerLimit, IntValue: 80))
            .IsOk.Should().BeTrue();

    [Fact]
    public void E3_power_limit_out_of_range_is_denied() =>
        _allow.Validate(new ApplyNvApiPowerLimitOp(0, PowerLimitPercent: 150))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- E4 fan curve. ---

    [Fact]
    public void E4_safe_fan_curve_is_allowlisted() =>
        _allow.Validate(new ApplyGpuFanCurveOp(0, new[]
        {
            new FanCurvePointDto(40, 35),
            new FanCurvePointDto(80, 60),
            new FanCurvePointDto(85, 75),
        })).IsOk.Should().BeTrue();

    /// <summary>24/09/2026 : seuls les points chauds étaient contrôlés. Ces deux courbes passaient
    /// et laissaient le ventilateur sous 40 % au-dessus de 80 °C, que le pilote tienne ou interpole.
    /// Règle prudente : le point qui gouverne 80 °C tient aussi le plancher (une courbe sûre en
    /// interpolation seule peut être refusée ; jamais l'inverse).</summary>
    [Theory]
    [InlineData(79, 10, 100, 40)]   // à 85 °C : 10 % tenu, ~17 % interpolé
    [InlineData(60, 20, -1, -1)]    // un seul point froid : 20 % tenu à 90 °C
    public void E4_curve_below_the_floor_just_above_80C_is_denied(int t1, int d1, int t2, int d2)
    {
        var points = t2 < 0
            ? new[] { new FanCurvePointDto(t1, d1) }
            : new[] { new FanCurvePointDto(t1, d1), new FanCurvePointDto(t2, d2) };
        _allow.Validate(new ApplyGpuFanCurveOp(0, points)).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void E4_hot_silent_fan_curve_is_denied() =>
        // The helper re-checks anti-overheat leaf-exact: a hot, near-silent point is refused.
        _allow.Validate(new ApplyGpuFanCurveOp(0, new[]
        {
            new FanCurvePointDto(85, 10),
        })).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void E4_empty_fan_curve_is_denied() =>
        _allow.Validate(new ApplyGpuFanCurveOp(0, Array.Empty<FanCurvePointDto>()))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void E4_out_of_range_duty_is_denied() =>
        _allow.Validate(new ApplyGpuFanCurveOp(0, new[] { new FanCurvePointDto(60, 130) }))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- Codec round-trip of the new fan-curve leaf (bounded, no type-name handling). ---

    [Fact]
    public void E4_fan_curve_op_roundtrips_through_the_bounded_codec()
    {
        var op = new ApplyGpuFanCurveOp(0, new[] { new FanCurvePointDto(40, 30), new FanCurvePointDto(85, 70) });
        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue();

        var back = PrivilegedOpCodec.Deserialize(json.Value!);
        back.IsOk.Should().BeTrue();
        var decoded = back.Value.Should().BeOfType<ApplyGpuFanCurveOp>().Subject;
        decoded.GpuIndex.Should().Be(0);
        decoded.Points.Should().HaveCount(2);
        decoded.Points.Should().Contain(p => p.TempC == 85 && p.DutyPct == 70);
    }
}
