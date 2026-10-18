using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// PreferMaxPerfTweak (NVIDIA « Power management mode » = Prefer maximum performance).
//
// Un seul réglage DRS (PREFERRED_PSTATE_ID = 0x1057EB71) → PREFER_MAX = 1. Compromis choisi
// (force les horloges max ; n'aide que si la carte baisse ses horloges en jeu, coûte au repos) :
// filtré sur un GPU de rendu NVIDIA de bureau, origine capturée pour un retour fidèle, appliqué via une op DRS typée.
// -----------------------------------------------------------------------
public sealed class PreferMaxPerfTweakTests
{
    // « Normal » du panneau NVIDIA vaut PREFERRED_PSTATE_ADAPTIVE (0), pas DRIVER_CONTROLLED (2) ni
    // OPTIMAL_POWER (5), le défaut de l'en-tête (NvApiDriverSettings.h ; fiche NVIDIA 3130, « Normal » ex-« Adaptive »).
    [Fact]
    public void Normal_est_la_valeur_adaptive_du_pilote()
    {
        PreferMaxPerfTweak.Normal.Should().Be(0u);
        PreferMaxPerfTweak.Normal.Should().NotBe(PreferMaxPerfTweak.PreferMax, "témoin : les deux modes diffèrent");
    }

    private const uint PreferredPstateId = 0x1057EB71;

    private readonly INvApi _nvApi = Substitute.For<INvApi>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    /// <summary>Etat par defaut : le mode de gestion d'alimentation du pilote est LU et vaut
    /// « Adaptive » — donc il y a quelque chose a corriger.</summary>
    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On,
        NvidiaPreferMaxPerf: TriState.Off);

    private static HardwareInventory Desktop(string gpuVendor = "NVIDIA", ChassisType chassis = ChassisType.Desktop) => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo("GPU", gpuVendor, 12L << 30, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        chassis);

    private PreferMaxPerfTweak Build(uint originalPstate = 0 /* adaptive */)
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetPreferredPstate().Returns(Result<uint>.Ok(originalPstate));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new PreferMaxPerfTweak(_nvApi, _elevated);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("latency.prefer_max_perf");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.SummaryKey.Should().NotContain("fps");
    }

    // --- Filtre : GPU de rendu NVIDIA de bureau seulement (ni portable/batterie, ni hors NVIDIA). ---

    [Fact]
    public void AppliesTo_true_on_desktop_nvidia() =>
        Build().AppliesTo(Desktop(), State).Should().BeTrue();

    [Fact]
    public void AppliesTo_false_on_non_nvidia_gpu() =>
        Build().AppliesTo(Desktop(gpuVendor: "AMD"), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_laptop() =>
        Build().AppliesTo(Desktop(chassis: ChassisType.Laptop), State).Should().BeFalse();

    /// <summary>
    /// Le placebo que ce test ferme : `PREFERRED_PSTATE` vaut deja 1
    /// (releve sur la machine de reference le 10/09/2026). Un gate qui ne regarde que « bureau + NVIDIA » proposerait ce levier, l'appliquerait
    /// sans rien changer, puis mesurerait le bruit d'une session. Un levier qui n'a rien a changer n'est
    /// pas un candidat.
    /// </summary>
    [Fact]
    public void AppliesTo_false_quand_le_mode_maximum_est_DEJA_actif() =>
        Build().AppliesTo(Desktop(), State with { NvidiaPreferMaxPerf = TriState.On })
            .Should().BeFalse();

    /// <summary>Unknown ne se corrige pas : sans lecture de l'etat courant, aucun candidat.</summary>
    [Fact]
    public void AppliesTo_false_quand_l_etat_courant_n_a_pas_ete_LU() =>
        Build().AppliesTo(Desktop(), State with { NvidiaPreferMaxPerf = TriState.Unknown })
            .Should().BeFalse();

    // --- Apply. ---

    [Fact]
    public async Task Apply_sets_prefer_max_on_the_pstate_setting()
    {
        var t = Build();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == PreferredPstateId && o.Value == 1u),
            Arg.Any<CancellationToken>());
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
    public async Task Apply_fails_clean_when_original_pstate_unreadable()
    {
        var t = Build();
        _nvApi.GetPreferredPstate().Returns(Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "no read"));

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
        _nvApi.GetPreferredPstate().Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // --- Le retour arrière rend la préférence de pstate d'origine capturée. ---

    [Fact]
    public async Task Apply_then_revert_restores_the_original_pstate()
    {
        var t = Build(originalPstate: 0); // adaptatif à l'origine

        var apply = await t.ApplyAsync();
        apply.IsOk.Should().BeTrue();

        _elevated.ClearReceivedCalls();
        var revert = await t.RevertAsync(apply.Value!);

        revert.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.SettingId == PreferredPstateId && o.Value == 0u),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("latency.prefer_max_perf", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- Ecrire au profil du JEU, revenir selon la PROVENANCE. ---
    //
    // Reecrire l'ancienne valeur dans un profil de jeu qui ne la possedait pas laisse une trace
    // que le jeu n'avait pas : la valeur revient, l'etat non, et le scan suivant dirait « pose par
    // toi » a quelqu'un qui n'a rien pose.

    private const string Jeu = @"C:\Games\Call of Duty\cod.exe";

    private sealed class JeuFixe(string? chemin) : IGameExecutable
    {
        public string? RunningGameExecutablePath() => chemin;
    }

    /// <summary>Le produit cherche le jeu et ne le trouve pas (PC au repos, anti-triche
    /// qui refuse l'ouverture du processus). Écrire au global mettait la carte au maximum hors jeu
    /// toute la journée : rien ne s'écrit.</summary>
    [Fact]
    public async Task Jeu_cherche_mais_introuvable_n_ecrit_RIEN_au_global()
    {
        Build();
        var r = await new PreferMaxPerfTweak(_nvApi, _elevated, new JeuFixe(null)).ApplyAsync();

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    private sealed class PiloteAuJeu(uint valeur, DrsValueOrigin origine) : INvApi
    {
        public bool IsAvailable => true;
        public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
            => Result<(uint, DrsOriginState)>.Ok(
                (valeur, new DrsOriginState(origine, origine == DrsValueOrigin.SetByUser, "")));
        public Result<uint> GetPreferredPstate() => Result<uint>.Ok(9); // le global : ne doit pas servir
        public Result<uint> GetLowLatencyMode() => Result<uint>.Ok(0);
        public Result<uint> GetMaxPreRenderedFrames() => Result<uint>.Ok(0);
        public Result<uint> GetVsyncMode() => Result<uint>.Ok(0);
        public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(0);
        public Result<DrsOriginState> GetFrameRateLimitOrigin()
            => Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
    }

    private async Task<(Result<TweakSnapshot> Apply, PrivilegedOp? Inverse)> AppliquerAuJeu(
        uint valeur, DrsValueOrigin origine)
    {
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        var r = await new PreferMaxPerfTweak(new PiloteAuJeu(valeur, origine), _elevated, new JeuFixe(Jeu))
            .ApplyAsync();
        var inverse = r.IsOk ? PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value : null;
        return (r, inverse);
    }

    [Fact]
    public async Task L_ecriture_vise_le_profil_du_JEU_quand_il_est_connu()
    {
        var (r, _) = await AppliquerAuJeu(0, DrsValueOrigin.InheritedFromGlobalProfile);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiDrsSettingOp>(o => o.ProfileName == Jeu && o.Value == 1u),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DrsValueOrigin.InheritedFromGlobalProfile)]
    [InlineData(DrsValueOrigin.InheritedFromBaseProfile)]
    [InlineData(DrsValueOrigin.DriverDefault)]
    [InlineData(DrsValueOrigin.ShippedWithTheDriver)]
    public async Task Un_reglage_que_le_jeu_ne_POSSEDAIT_pas_se_rend_par_SUPPRESSION(DrsValueOrigin origine)
    {
        var (_, inverse) = await AppliquerAuJeu(0, origine);

        inverse.Should().BeOfType<DeleteNvApiDrsSettingOp>()
            .Which.Should().Be(new DeleteNvApiDrsSettingOp(PreferredPstateId, Jeu, origine, 0));
    }

    [Fact]
    public async Task Un_reglage_POSE_sur_le_profil_du_jeu_se_rend_par_REECRITURE()
    {
        var (_, inverse) = await AppliquerAuJeu(0, DrsValueOrigin.SetByUser);

        inverse.Should().Be(new ApplyNvApiDrsSettingOp(PreferredPstateId, 0, Jeu));
    }

    [Fact]
    public async Task Une_provenance_INCONNUE_n_ecrit_rien_nulle_part()
    {
        var (r, _) = await AppliquerAuJeu(0, DrsValueOrigin.Unknown);

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
    // Au profil global, réglage au défaut du pilote : l'origine exacte serait « absent », que seule une suppression au profil
    // de base rendrait, et la liste blanche n'en admet aucune. Rien n'est écrit, et c'est dit.
    [Fact]
    public async Task Au_defaut_du_pilote_au_profil_global_rien_n_est_ecrit()
    {
        var t = Build();
        _nvApi.GetPreferredPstate().Returns(Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "absent du profil"));
        _nvApi.PreferredPstateAuDefaut().Returns(true);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("driver default");
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
