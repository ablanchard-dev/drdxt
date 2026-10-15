using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Garde : les leviers de latence ne peuvent appliquer que les ops que la liste blanche permet.
//
// Ces tests reconstruisent les ops typées exactes qu'émettent les leviers de latence (mêmes
// coordonnées de feuille et mêmes genres que les leviers de production) et vérifient
// qu'OpAllowlist les accepte. Si un levier dérive vers une cible hors liste, la branche
// correspondante est le canari : on change la liste blanche à dessein (feuille exacte),
// jamais la cible du levier en silence.
// -----------------------------------------------------------------------
public sealed class LatencyTweakOpsAllowlistTests
{
    private const string NvTweakPath = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private readonly OpAllowlist _allow = new();

    // --- Plancher de latence pilote Reflex (NVTweak\LowLatencyMode). ---

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

    // --- Plafond de file de rendu (AMD Anti-Lag, NVIDIA Max-Pre-Rendered-Frames). ---

    [Fact]
    public void D2_amd_antilag_op_is_allowlisted() =>
        _allow.Validate(new ApplyAdlxTuningOp(GpuIndex: 0, AdlxTuningKind.AntiLag, IntValue: 1))
            .IsOk.Should().BeTrue();

    [Fact]
    public void D2_nvidia_render_queue_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x007ba09e, 1, "Base Profile"))
            .IsOk.Should().BeTrue();

    // --- VRR + Vsync + plafond (DRS Vsync + limite de cadence). Identifiants corrigés. ---

    [Fact]
    public void D3_vsync_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x00A879CF, 0x47814940, "Base Profile"))
            .IsOk.Should().BeTrue();

    [Fact]
    public void D3_frame_rate_limit_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002, 141, "Base Profile"))
            .IsOk.Should().BeTrue();

    // --- Préférer la performance maximale (PREFERRED_PSTATE, « Power management mode »). ---

    [Fact]
    public void Ns2_prefer_max_perf_pstate_drs_op_is_allowlisted() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x1057EB71, 1, "Base Profile"))
            .IsOk.Should().BeTrue();

    // --- Garde négative : un profil DRS vide (que les leviers ne produisent jamais) est refusé. ---

    [Fact]
    public void Blank_drs_profile_is_denied() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x007ba09e, 1, ""))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- Ensemble fermé de SettingId DRS, feuille exacte : les 3 identifiants connus passent,
    //     tout autre identifiant (forgé) est refusé même avec un nom de profil valide. C'est le
    //     test négatif : la liste blanche doit être un ensemble fermé, pas « n'importe quel id ». ---

    [Theory]
    [InlineData(0x007BA09Eu)] // limite de pré-rendu (Max-Pre-Rendered-Frames)
    [InlineData(0x00A879CFu)] // VSYNCMODE (corrigé)
    [InlineData(0x10835002u)] // FRL_FPS / limiteur de cadence (corrigé)
    [InlineData(0x1057EB71u)] // PREFERRED_PSTATE / mode de gestion d'énergie
    public void Known_drs_setting_ids_are_allowlisted(uint settingId) =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(settingId, ValeurNormale(settingId), "Base Profile"))
            .IsOk.Should().BeTrue();

    [Fact]
    public void Forged_drs_setting_id_is_denied_even_with_valid_profile() =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(0xDEADBEEFu, 1, "Base Profile"))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- Garde de régression : les anciens identifiants erronés, remplacés par les valeurs
    //     vérifiées dans le NvApiDriverSettings.h officiel de NVIDIA, doivent rester refusés.
    //     En réintroduire un écrirait en silence un réglage pilote inconnu. ---
    [Theory]
    [InlineData(0x10835000u)] // ancien FRL erroné (décalé de 2 par rapport à FRL_FPS_ID = 0x10835002)
    [InlineData(0x00fc00fau)] // ancien VSYNC erroné (remplacé par VSYNCMODE_ID = 0x00A879CF)
    public void Superseded_wrong_drs_ids_stay_denied(uint wrongId) =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(wrongId, 1, "Base Profile"))
            .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // Une valeur qu'un réglage normal porte, par identifiant (la liste blanche borne aussi la valeur).
    private static uint ValeurNormale(uint id) => id switch { 0x00A879CFu => 0x47814940u, 0x10835002u => 141u, _ => 1u };
}
