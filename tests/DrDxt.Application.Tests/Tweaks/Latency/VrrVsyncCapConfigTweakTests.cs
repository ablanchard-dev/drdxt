using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// M2 Phase D3 — VrrVsyncCapConfigTweak.
//
// Configures the Blur-Busters low-latency combo at the DRIVER level: Vsync ON +
// a frame cap ~3 fps under the refresh, for a VRR/G-Sync/FreeSync display. NO
// blind cap — AppliesTo is false when no VRR display is detected (IDisplayInfo).
// Captures the original Vsync + frame-limit as a composite inverse snapshot;
// applies via IElevatedClient typed DRS ops; fails clean if the SDK is missing.
// -----------------------------------------------------------------------
public sealed class VrrVsyncCapConfigTweakTests
{
    private readonly INvApi _nvApi = Substitute.For<INvApi>();
    private readonly IDisplayInfo _display = Substitute.For<IDisplayInfo>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory DesktopNvidia() => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        ChassisType.Desktop);

    private VrrVsyncCapConfigTweak Build(bool vrr = true, int refresh = 144)
    {
        _display.GetPrimaryGameDisplay()
            .Returns(Result<DisplayInfo>.Ok(new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: vrr, RefreshHz: refresh)));
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetVsyncMode().Returns(Result<uint>.Ok(0));      // vsync off originally
        _nvApi.GetFrameRateLimit().Returns(Result<uint>.Ok(0)); // no cap originally
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new VrrVsyncCapConfigTweak(_nvApi, _display, _elevated);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("latency.vrr_vsync_cap");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.SummaryKey.Should().NotContain("fps");
    }

    // --- Gating: VRR display required, else skip (no blind cap). ---

    // LAP1 (08/10/2026) : réglages du pilote, réversibles ; ils marchent aussi sur une carte de portable.
    [Fact]
    public void LAP1_propose_sur_un_portable_NVIDIA_avec_ecran_VRR() =>
        Build(vrr: true).AppliesTo(DesktopNvidia() with
        {
            Chassis = ChassisType.Laptop,
            Gpus = new[] { new GpuInfo("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", 8L << 30, "551.23", TriState.On) },
        }, State).Should().BeTrue();

    [Fact]
    public void Jamais_sur_une_console_portable() =>
        Build(vrr: true).AppliesTo(DesktopNvidia() with { Chassis = ChassisType.Handheld }, State).Should().BeFalse();

    [Fact]
    public void AppliesTo_true_when_vrr_display_present() =>
        Build(vrr: true).AppliesTo(DesktopNvidia(), State).Should().BeTrue();

    [Fact]
    public void AppliesTo_false_when_no_vrr_display() =>
        Build(vrr: false).AppliesTo(DesktopNvidia(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_when_display_info_unavailable()
    {
        var t = Build();
        _display.GetPrimaryGameDisplay()
            .Returns(Result<DisplayInfo>.Fail(ErrorCodes.TweakSdkUnavailable, "no display info"));

        t.AppliesTo(DesktopNvidia(), State).Should().BeFalse();
    }

    // --- Apply. ---

    [Fact]
    public async Task Apply_configures_vsync_and_cap_three_under_refresh()
    {
        var t = Build(refresh: 144); // cap should be 138 (PlafondVrr : Reflex, 09/10/2026)

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Vsync ON via DRS and frame cap = PlafondVrr.Cible(144) = 138 via DRS.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.Value == 138),
            Arg.Any<CancellationToken>());
        // Two settings applied (vsync + cap).
        await _elevated.Received(2).ExecuteAsync(Arg.Any<ApplyNvApiDrsSettingOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_clamps_cap_to_frl_documented_max_1023()
    {
        // FRL_FPS is documented (NVIDIA NvApiDriverSettings.h) as a 0-1023 range. A bogus
        // refresh from EDID/driver must never push an out-of-range value to the driver.
        var t = Build(refresh: 2000); // a bogus refresh: the target must stay inside the FRL range (PlafondVrr gives 1000)

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == 0x10835002u && o.Value == 1000u && o.Value <= 1023u),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_fails_clean_when_display_info_unavailable_at_apply()
    {
        var t = Build();
        _display.GetPrimaryGameDisplay()
            .Returns(Result<DisplayInfo>.Fail(ErrorCodes.TweakSdkUnavailable, "no display info"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_skips_not_applicable_when_no_vrr_at_apply()
    {
        // Defensive: never apply a blind cap on a non-VRR panel even if called directly.
        var t = Build(vrr: false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakNotApplicable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_fails_clean_when_nvapi_unavailable()
    {
        var t = Build();
        _nvApi.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "boom")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _nvApi.GetVsyncMode().Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public async Task Apply_rolls_back_vsync_when_the_frame_cap_op_fails()
    {
        // §6.2 "rien ne reste pendu": op (1) Vsync ON succeeds, op (2) frame cap
        // FAILS → the tweak must replay the inverse of op (1) (restore Vsync to the
        // captured original 0) and return Fail, leaving NO half-applied state.
        var t = Build(refresh: 144); // cap target = 138
        _nvApi.GetVsyncMode().Returns(Result<uint>.Ok(0));      // original vsync = 0
        _nvApi.GetFrameRateLimit().Returns(Result<uint>.Ok(0)); // original cap = 0

        // Vsync ON (VSYNCMODE_FORCEON) succeeds; the frame cap (value 138) fails.
        // Corrected DRS ids: VSYNCMODE = 0x00A879CF, FRL_FPS = 0x10835002.
        _elevated.ExecuteAsync(
                Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == 0x00A879CFu && o.Value == 0x47814940u),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _elevated.ExecuteAsync(
                Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == 0x10835002u && o.Value == 138u),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "cap write failed")));
        // The inverse rollback (Vsync back to 0) is allowed to succeed.
        _elevated.ExecuteAsync(
                Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == 0x00A879CFu && o.Value == 0u),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));

        var r = await t.ApplyAsync();

        // Returns Fail (the original failure code), not Ok.
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        // The already-applied op (1)'s inverse WAS replayed: Vsync restored to 0.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == 0x00A879CFu && o.Value == 0u),
            Arg.Any<CancellationToken>());
    }

    // --- Revert: composite snapshot restores both original settings. ---

    [Fact]
    public async Task Apply_then_revert_restores_both_original_settings()
    {
        var t = Build(refresh: 144);
        _nvApi.GetVsyncMode().Returns(Result<uint>.Ok(0));
        _nvApi.GetFrameRateLimit().Returns(Result<uint>.Ok(0));

        var apply = await t.ApplyAsync();
        apply.IsOk.Should().BeTrue();

        _elevated.ClearReceivedCalls();
        var revert = await t.RevertAsync(apply.Value!);

        revert.IsOk.Should().BeTrue();
        // Both original values (0 vsync, 0 cap) replayed via the helper.
        await _elevated.Received(2).ExecuteAsync(Arg.Any<ApplyNvApiDrsSettingOp>(), Arg.Any<CancellationToken>());
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.Value == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("latency.vrr_vsync_cap", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- 14/09/2026, plan complet 1.3 : le combo s'ecrit au profil du JEU, plus au pilote entier. ---
    //
    // Ecrits dans « Base Profile », la synchro verticale et le plafond touchaient tous les jeux et
    // le bureau, pour une mesure faite sur un seul. PreferMaxPerf ecrivait deja par jeu ; ce levier non.

    private const uint VsyncId = 0x00A879CFu;
    private const uint FrlId = 0x10835002u;
    private const string Jeu = @"C:\Games\Call of Duty\cod.exe";

    private sealed class JeuFixe(string? chemin) : IGameExecutable
    {
        public string? RunningGameExecutablePath() => chemin;
    }

    /// <summary>Le pilote tel qu'il repond pour le jeu : chaque reglage vaut 7, avec la provenance
    /// donnee. Le global vaut 9, et ne doit servir que si le jeu n'a pas de profil lisible.</summary>
    /// <param name="echec">Le code quand le jeu n'est pas lisible : pas de profil (defaut), ou illisible.</param>
    private sealed class PiloteAuJeu(DrsValueOrigin origine, bool lisibleAuJeu = true,
        string echec = ErrorCodes.DrsNoGameProfile) : INvApi
    {
        public bool IsAvailable => true;
        public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
            => lisibleAuJeu
                ? Result<(uint, DrsOriginState)>.Ok(
                    (7u, new DrsOriginState(origine, origine == DrsValueOrigin.SetByUser, "")))
                : Result<(uint, DrsOriginState)>.Fail(echec, "pas lu au jeu");
        public Result<uint> GetPreferredPstate() => Result<uint>.Ok(9);
        public Result<uint> GetLowLatencyMode() => Result<uint>.Ok(9);
        public Result<uint> GetMaxPreRenderedFrames() => Result<uint>.Ok(9);
        public Result<uint> GetVsyncMode() => Result<uint>.Ok(9);
        public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(9);
        public Result<DrsOriginState> GetFrameRateLimitOrigin()
            => Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
    }

    private async Task<(Result<TweakSnapshot> Apply, IReadOnlyList<PrivilegedOp> Inverses)> AppliquerAuJeu(
        DrsValueOrigin origine, bool lisibleAuJeu = true)
    {
        Build(refresh: 144);
        var r = await new VrrVsyncCapConfigTweak(
                new PiloteAuJeu(origine, lisibleAuJeu), _display, _elevated, new JeuFixe(Jeu))
            .ApplyAsync();
        var inverses = r.IsOk
            ? CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson).Value!
            : Array.Empty<PrivilegedOp>();
        return (r, inverses);
    }

    [Fact]
    public async Task Les_deux_ecritures_visent_le_profil_du_JEU_quand_il_est_connu()
    {
        var (r, _) = await AppliquerAuJeu(DrsValueOrigin.InheritedFromGlobalProfile);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(2).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.ProfileName == Jeu), Arg.Any<CancellationToken>());
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.ProfileName != Jeu), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DrsValueOrigin.InheritedFromGlobalProfile)]
    [InlineData(DrsValueOrigin.InheritedFromBaseProfile)]
    [InlineData(DrsValueOrigin.DriverDefault)]
    [InlineData(DrsValueOrigin.ShippedWithTheDriver)]
    public async Task Deux_reglages_que_le_jeu_ne_POSSEDAIT_pas_se_rendent_par_SUPPRESSION(DrsValueOrigin origine)
    {
        var (_, inverses) = await AppliquerAuJeu(origine);

        inverses.Should().Equal(
            new DeleteNvApiDrsSettingOp(FrlId, Jeu, origine, 7u),
            new DeleteNvApiDrsSettingOp(VsyncId, Jeu, origine, 7u));
    }

    [Fact]
    public async Task Deux_reglages_POSES_sur_le_profil_du_jeu_se_rendent_par_REECRITURE()
    {
        var (_, inverses) = await AppliquerAuJeu(DrsValueOrigin.SetByUser);

        inverses.Should().Equal(
            new ApplyNvApiDrsSettingOp(FrlId, 7u, Jeu),
            new ApplyNvApiDrsSettingOp(VsyncId, 7u, Jeu));
    }

    [Fact]
    public async Task Une_provenance_INCONNUE_n_ecrit_rien_nulle_part()
    {
        var (r, _) = await AppliquerAuJeu(DrsValueOrigin.Unknown);

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Le jeu repond pour la synchro verticale, pas pour le plafond.</summary>
    private sealed class PiloteMoitie : INvApi
    {
        public bool IsAvailable => true;
        public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
            => settingId == VsyncId
                ? Result<(uint, DrsOriginState)>.Ok(
                    (7u, new DrsOriginState(DrsValueOrigin.SetByUser, true, "")))
                : Result<(uint, DrsOriginState)>.Fail(ErrorCodes.DrsNoGameProfile, "pas de profil pour ce reglage");
        public Result<uint> GetPreferredPstate() => Result<uint>.Ok(9);
        public Result<uint> GetLowLatencyMode() => Result<uint>.Ok(9);
        public Result<uint> GetMaxPreRenderedFrames() => Result<uint>.Ok(9);
        public Result<uint> GetVsyncMode() => Result<uint>.Ok(9);
        public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(9);
        public Result<DrsOriginState> GetFrameRateLimitOrigin()
            => Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
    }

    [Fact]
    public async Task Un_combo_moitie_au_jeu_moitie_au_pilote_entier_n_ecrit_RIEN()
    {
        Build(refresh: 144);

        var r = await new VrrVsyncCapConfigTweak(new PiloteMoitie(), _display, _elevated, new JeuFixe(Jeu))
            .ApplyAsync();

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Un_jeu_connu_sans_profil_pilote_n_ecrit_RIEN_meme_au_global()
    {
        // 24/09/2026 : ce test figeait l'inverse (repli global). Un gain mesuré sur un jeu ne
        // s'écrit plus pour tout le PC : sur la machine de référence, le global portait déjà
        // « performances maximales », carte au maximum hors jeu toute la journée.
        var (r, _) = await AppliquerAuJeu(DrsValueOrigin.SetByUser, lisibleAuJeu: false);

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Un_profil_de_jeu_qui_existe_mais_ne_se_lit_pas_n_ecrit_RIEN_meme_au_global()
    {
        Build(refresh: 144);

        var r = await new VrrVsyncCapConfigTweak(
                new PiloteAuJeu(DrsValueOrigin.SetByUser, lisibleAuJeu: false, echec: ErrorCodes.TweakSdkUnavailable),
                _display, _elevated, new JeuFixe(Jeu))
            .ApplyAsync();

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
