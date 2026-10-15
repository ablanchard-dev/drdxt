using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// RenderQueueCapTweak.
//
// Le plancher universel de latence basse quand le chemin NVIDIA Reflex / Low Latency Mode
// n'est pas la voie : AMD via Anti-Lag ADLX, NVIDIA via le réglage DRS
// Max-Pre-Rendered-Frames = 1. Le filtre est complémentaire du plancher Reflex (qui possède
// NVIDIA sur bureau) : ce levier possède le GPU de rendu AMD de bureau. Capture la vraie
// origine → op inverse sérialisée ; applique via IElevatedClient ; échoue proprement sans SDK.
//
// Aucun bidouillage de registre non supporté (liste noire) : AMD → ApplyAdlxTuningOp, NVIDIA →
// ApplyNvApiDrsSettingOp, jamais une écriture brute d'une clé de registre non supportée.
// -----------------------------------------------------------------------
public sealed class RenderQueueCapTweakTests
{
    private readonly INvApi _nvApi = Substitute.For<INvApi>();
    private readonly IAdlx _adlx = Substitute.For<IAdlx>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Hw(string gpuModel, string gpuVendor, ChassisType chassis) => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo(gpuModel, gpuVendor, 16L << 30, "24.1", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        chassis);

    private static HardwareInventory DesktopAmd() => Hw("AMD Radeon RX 7800 XT", "AMD", ChassisType.Desktop);
    private static HardwareInventory DesktopNvidia() => Hw("NVIDIA GeForce RTX 4070", "NVIDIA", ChassisType.Desktop);
    private static HardwareInventory HandheldAmd() => Hw("AMD Custom GPU 0405", "AMD", ChassisType.Handheld);
    private static HardwareInventory LaptopAmd() => Hw("AMD Radeon RX 7600M XT", "AMD", ChassisType.Laptop);

    private static HardwareInventory IgpuOnly() => new(
        new CpuInfo("Ryzen 5 8600G", "AMD", 6, 12, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("AMD Radeon 760M Graphics", "AMD", 0, "24.1", TriState.Unknown) },
        new[] { new MemoryModule("PN", "Corsair", 5600, 5600) },
        new MotherboardInfo("ASRock", "B650"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private RenderQueueCapTweak Build()
    {
        _adlx.IsAvailable.Returns(true);
        _adlx.GetAntiLagState().Returns(Result<int>.Ok(0)); // désactivé à l'origine
        _adlx.GetChillActif().Returns(Result<bool>.Ok(false));
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetMaxPreRenderedFrames().Returns(Result<uint>.Ok(3)); // profondeur de file par défaut
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new RenderQueueCapTweak(_nvApi, _adlx, _elevated);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("latency.render_queue_cap");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.SummaryKey.Should().NotContain("fps");
    }

    // --- Filtre : complémentaire du plancher Reflex (qui possède NVIDIA sur bureau). ---

    // L'écriture d'Anti-Lag n'existe pas encore dans le helper ; proposé, le levier ne
    // pouvait qu'échouer. Il revient quand elle est écrite et prouvée sur une vraie carte AMD.
    // L'écriture d'Anti-Lag existe ; proposé sur AMD quand Anti-Lag ET Chill sont lus éteints.
    [Fact]
    public void AppliesTo_true_on_desktop_amd_when_anti_lag_and_chill_read_off() =>
        Build().AppliesTo(DesktopAmd(), State).Should().BeTrue();

    [Fact]
    public void AppliesTo_false_when_anti_lag_already_on()
    {
        var t = Build();
        _adlx.GetAntiLagState().Returns(Result<int>.Ok(1));
        t.AppliesTo(DesktopAmd(), State).Should().BeFalse();
    }

    // Allumer Anti-Lag éteint Chill, et le rendre ne le rallume pas : Chill allumé ou illisible ferme le levier.
    [Fact]
    public void AppliesTo_false_when_chill_on_and_unread_chill_is_said()
    {
        var t = Build();
        _adlx.GetChillActif().Returns(Result<bool>.Ok(true));
        t.AppliesTo(DesktopAmd(), State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeFalse();

        _adlx.GetChillActif().Returns(Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu"));
        t.AppliesTo(DesktopAmd(), State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_refuses_and_writes_nothing_when_chill_is_on()
    {
        var t = Build();
        _adlx.GetChillActif().Returns(Result<bool>.Ok(true));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AppliesTo_false_on_desktop_nvidia_without_a_known_game() =>
        Build().AppliesTo(DesktopNvidia(), State).Should().BeFalse();

    // ── V-NV1 : « Low Latency Mode : On » sur NVIDIA, au profil du jeu, jamais pour un jeu à Reflex. ──
    private const string JeuSansReflex = @"C:\Games\Hades II\Hades2.exe";

    private RenderQueueCapTweak SurNvidia(string? jeu, Result<(uint Value, DrsOriginState Origin)> lu)
    {
        Build();
        _adlx.IsAvailable.Returns(false);
        if (jeu is not null) _nvApi.GetDrsForApplication(0x007ba09eu, jeu).Returns(lu);
        return new RenderQueueCapTweak(_nvApi, _adlx, _elevated, new JeuFixe(jeu));
    }

    private static Result<(uint Value, DrsOriginState Origin)> Lu(uint v) =>
        Result<(uint, DrsOriginState)>.Ok((v, new DrsOriginState(DrsValueOrigin.InheritedFromGlobalProfile, false, "")));

    [Fact]
    public void Sur_NVIDIA_propose_la_file_a_1_pour_un_jeu_sans_Reflex_dont_le_profil_se_lit()
    {
        var t = SurNvidia(JeuSansReflex, Lu(0)); // 0 = laissé à l'application
        t.AppliesTo(DesktopNvidia(), State).Should().BeTrue();
        t.EtatNonLu(State).Should().BeFalse();
    }

    // Scanné sans jeu ouvert, la file NVIDIA n'est pas lue, et c'est dit.
    [Fact]
    public void Sur_NVIDIA_sans_jeu_ouvert_la_file_est_dite_non_lue()
    {
        var t = SurNvidia(null, Lu(0));
        t.AppliesTo(DesktopNvidia(), State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeTrue();
    }

    [Fact]
    public void Sur_NVIDIA_rien_si_la_file_est_deja_a_1() =>
        SurNvidia(JeuSansReflex, Lu(1)).AppliesTo(DesktopNvidia(), State).Should().BeFalse();

    [Theory]
    [InlineData(@"C:\Games\Call of Duty\cod.exe")]
    [InlineData(@"D:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe")]
    [InlineData(@"C:\Ubisoft\Rainbow Six Siege\RainbowSix_DX12.exe")]
    public void Sur_NVIDIA_jamais_pour_un_jeu_qui_a_Reflex(string jeu)
    {
        var t = SurNvidia(jeu, Lu(0));
        t.AppliesTo(DesktopNvidia(), State).Should().BeFalse("Reflex remplace la file du pilote quand le jeu l'active");
        t.EtatNonLu(State).Should().BeFalse();
    }

    [Fact]
    public void Sur_NVIDIA_un_jeu_sans_profil_pilote_n_est_pas_un_etat_non_lu()
    {
        var t = SurNvidia(JeuSansReflex, Result<(uint, DrsOriginState)>.Fail(ErrorCodes.DrsNoGameProfile, "pas de profil"));
        t.AppliesTo(DesktopNvidia(), State).Should().BeFalse("sans profil, rien ne s'écrit (pas de repli global)");
        t.EtatNonLu(State).Should().BeFalse();
    }

    [Fact]
    public void Sur_NVIDIA_un_profil_illisible_est_dit()
    {
        var t = SurNvidia(JeuSansReflex, Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable, "illisible"));
        t.AppliesTo(DesktopNvidia(), State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_false_on_handheld() =>
        Build().AppliesTo(HandheldAmd(), State).Should().BeFalse();

    [Fact]
    public void LAP1_Anti_Lag_est_propose_sur_un_portable_AMD() =>
        Build().AppliesTo(LaptopAmd(), State).Should().BeTrue();

    [Fact]
    public void LAP1_la_file_NVIDIA_est_proposee_sur_une_carte_de_portable() =>
        SurNvidia(JeuSansReflex, Lu(0))
            .AppliesTo(Hw("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", ChassisType.Laptop), State).Should().BeTrue();

    [Fact]
    public void AppliesTo_false_on_igpu_only() =>
        Build().AppliesTo(IgpuOnly(), State).Should().BeFalse();

    // --- Application (chemin Anti-Lag AMD). ---

    [Fact]
    public async Task Apply_enables_antilag_via_helper_and_captures_original()
    {
        var t = Build();
        _adlx.GetAntiLagState().Returns(Result<int>.Ok(0)); // désactivé → on active

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.Kind == AdlxTuningKind.AntiLag && o.IntValue == 1),
            Arg.Any<CancellationToken>());
        // L'instantané porte l'origine capturée (0) en op ADLX inverse.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>()
            .Which.IntValue.Should().Be(0);
        // La valeur posée, sans laquelle la désinstallation ne rendrait jamais Anti-Lag.
        PrivilegedOpCodec.Deserialize(r.Value!.AppliedValueJson).Value.Should()
            .Be(new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 1));
    }

    // Une origine hors de l'énumération documentée ne se rejouerait pas (le helper la refuse) : rien n'est écrit, sinon le
    // réglage de DrDXT resterait posé pour toujours.
    [Fact]
    public async Task Une_origine_irrejouable_n_est_pas_ecrasee()
    {
        var t = Build();
        _adlx.IsAvailable.Returns(false);
        _nvApi.GetMaxPreRenderedFrames().Returns(Result<uint>.Ok(0x100));

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<ApplyNvApiDrsSettingOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_falls_back_to_nvidia_drs_when_only_nvapi_available()
    {
        // La branche NVIDIA universelle : plafond de file de rendu via DRS Max-Pre-Rendered-Frames = 1.
        var t = Build();
        _adlx.IsAvailable.Returns(false);
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetMaxPreRenderedFrames().Returns(Result<uint>.Ok(3));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.Value == 1 && !string.IsNullOrWhiteSpace(o.ProfileName)),
            Arg.Any<CancellationToken>());
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.Value.Should().BeOfType<ApplyNvApiDrsSettingOp>()
            .Which.Value.Should().Be(3);
    }

    [Fact]
    public async Task Apply_fails_clean_when_no_sdk_available()
    {
        var t = Build();
        _adlx.IsAvailable.Returns(false);
        _nvApi.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_fails_clean_when_capture_fails()
    {
        var t = Build();
        _adlx.GetAntiLagState().Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no adlx"));

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
        _adlx.GetAntiLagState().Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // --- Revert. ---

    [Fact]
    public async Task Revert_restores_captured_original_antilag()
    {
        var t = Build();
        var original = new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 0);
        var snap = new TweakSnapshot("latency.render_queue_cap", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.Kind == AdlxTuningKind.AntiLag && o.IntValue == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("latency.render_queue_cap", "{not json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
    }

    // --- 14/09/2026, plan complet 1.3 : la branche NVIDIA ecrit au profil du JEU. ---
    //
    // Elle ecrivait « Max Pre-Rendered Frames » dans « Base Profile » : tous les jeux et le bureau.

    private const uint PreRenderId = 0x007ba09eu;
    private const string Jeu = @"C:\Games\Call of Duty\cod.exe";

    private sealed class JeuFixe(string? chemin) : IGameExecutable
    {
        public string? RunningGameExecutablePath() => chemin;
    }

    private sealed class PiloteAuJeu(DrsValueOrigin origine) : INvApi
    {
        public bool IsAvailable => true;
        public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
            => Result<(uint, DrsOriginState)>.Ok(
                (7u, new DrsOriginState(origine, origine == DrsValueOrigin.SetByUser, "")));
        public Result<uint> GetPreferredPstate() => Result<uint>.Ok(9);
        public Result<uint> GetLowLatencyMode() => Result<uint>.Ok(9);
        public Result<uint> GetMaxPreRenderedFrames() => Result<uint>.Ok(9); // le global : ne doit pas servir
        public Result<uint> GetVsyncMode() => Result<uint>.Ok(9);
        public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(9);
        public Result<DrsOriginState> GetFrameRateLimitOrigin()
            => Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
    }

    private async Task<(Result<TweakSnapshot> Apply, PrivilegedOp? Inverse)> AppliquerAuJeu(DrsValueOrigin origine)
    {
        Build();
        _adlx.IsAvailable.Returns(false); // la branche NVIDIA
        var r = await new RenderQueueCapTweak(new PiloteAuJeu(origine), _adlx, _elevated, new JeuFixe(Jeu))
            .ApplyAsync();
        return (r, r.IsOk ? PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value : null);
    }

    [Fact]
    public async Task La_branche_NVIDIA_ecrit_au_profil_du_JEU_quand_il_est_connu()
    {
        var (r, _) = await AppliquerAuJeu(DrsValueOrigin.InheritedFromGlobalProfile);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.ProfileName == Jeu && o.Value == 1u),
            Arg.Any<CancellationToken>());
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.ProfileName != Jeu), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Un_reglage_que_le_jeu_ne_POSSEDAIT_pas_se_rend_par_SUPPRESSION()
    {
        var (_, inverse) = await AppliquerAuJeu(DrsValueOrigin.InheritedFromGlobalProfile);

        inverse.Should().Be(
            new DeleteNvApiDrsSettingOp(PreRenderId, Jeu, DrsValueOrigin.InheritedFromGlobalProfile, 7u));
    }

    [Fact]
    public async Task Une_provenance_INCONNUE_n_ecrit_rien_nulle_part()
    {
        var (r, _) = await AppliquerAuJeu(DrsValueOrigin.Unknown);

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
