using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Guard: the M2 Phase D latency tweaks may only apply ops the allowlist permits.
//
// These tests reconstruct the exact typed ops the D1/D2/D3 tweaks emit (same leaf
// coordinates / kinds the production tweaks use) and assert OpAllowlist accepts
// them. If a tweak ever drifts to a non-allowlisted target, the corresponding
// arm here is the canary — change the allowlist intentionally (leaf-exact), never
// the tweak's target silently.
// -----------------------------------------------------------------------
public sealed class LatencyTweakOpsAllowlistTests
{
    private const string NvTweakPath = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private readonly OpAllowlist _allow = new();

    // --- D1: Reflex driver low-latency floor (NVTweak\LowLatencyMode). ---

    [Fact]
    public void D1_set_low_latency_mode_is_allowlisted() =>
        _allow.Validate(new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, NvTweakPath, "LowLatencyMode", RegistryValueType.DWord, "2"))
            .IsOk.Should().BeTrue();

    [Fact]
    public void D1_delete_low_latency_mode_inverse_is_allowlisted() =>
        _allow.Validate(new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine, NvTweakPath, "LowLatencyMode"))
            .IsOk.Should().BeTrue();

    // --- D2: render-queue cap (AMD Anti-Lag, NVIDIA Max-Pre-Rendered-Frames). ---

    [Fact]
    public void D2_amd_antilag_op_is_allowlisted() =>
        _allow.Validate(new ApplyAdlxTuningOp(GpuIndex: 0, AdlxTuningKind.AntiLag, IntValue: 1))
            .IsOk.Should().BeTrue();

    [Fact]
    public void D2_nvidia_render_queue_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x007ba09e, 1, "Base Profile"))
            .IsOk.Should().BeTrue();

    // --- D3: VRR + Vsync + cap (DRS Vsync + frame-rate limit). CORRECTED ids. ---

    [Fact]
    public void D3_vsync_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x00A879CF, 1, "Base Profile"))
            .IsOk.Should().BeTrue();

    [Fact]
    public void D3_frame_rate_limit_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002, 141, "Base Profile"))
            .IsOk.Should().BeTrue();

    // --- NS-2: Prefer maximum performance (PREFERRED_PSTATE, "Power management mode"). ---

    [Fact]
    public void Ns2_prefer_max_perf_pstate_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x1057EB71, 1, "Base Profile"))
            .IsOk.Should().BeTrue();

    // --- Negative guard: a blank DRS profile (which the tweaks never produce) is denied. ---

    [Fact]
    public void Blank_drs_profile_is_denied() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x007ba09e, 1, ""))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- Leaf-exact DRS SettingId allow-set (§9.13): the 3 known ids pass, any
    //     other (forged) id is DENIED even with a valid profile name. This is the
    //     missing negative test — the allowlist must be a CLOSED set, not "any id". ---

    [Theory]
    [InlineData(0x007BA09Eu)] // pre-render-limit (D2 Max-Pre-Rendered-Frames)
    [InlineData(0x00A879CFu)] // VSYNCMODE (D3, corrected)
    [InlineData(0x10835002u)] // FRL_FPS / frame-rate limiter (D3, corrected)
    [InlineData(0x1057EB71u)] // PREFERRED_PSTATE / power management mode (NS-2)
    public void Known_drs_setting_ids_are_allowlisted(uint settingId) =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(settingId, 1, "Base Profile"))
            .IsOk.Should().BeTrue();

    [Fact]
    public void Forged_drs_setting_id_is_denied_even_with_valid_profile() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0xDEADBEEFu, 1, "Base Profile"))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- Regression guard (audit C3): the historical WRONG ids — superseded by the
    //     values verified against NVIDIA's official NvApiDriverSettings.h — must STAY
    //     denied. Re-introducing one would silently write an unknown driver setting. ---
    [Theory]
    [InlineData(0x10835000u)] // old wrong FRL (off-by-2 from FRL_FPS_ID = 0x10835002)
    [InlineData(0x00fc00fau)] // old wrong VSYNC (superseded by VSYNCMODE_ID = 0x00A879CF)
    public void Superseded_wrong_drs_ids_stay_denied(uint wrongId) =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(wrongId, 1, "Base Profile"))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
}
