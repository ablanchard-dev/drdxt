using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// Sur une carte AMD (sans NVAPI), le plafond VRR passe par FRTC : par NVAPI, le levier
// serait proposé puis ne pourrait qu'échouer.
public sealed class VrrVsyncCapAmdTests
{
    private readonly INvApi _nvApi = Substitute.For<INvApi>();
    private readonly IAdlx _adlx = Substitute.For<IAdlx>();
    private readonly IDisplayInfo _display = Substitute.For<IDisplayInfo>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory DesktopAmd() => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo("AMD Radeon RX 7800 XT", "AMD", 16L << 30, "24.9.1", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        ChassisType.Desktop);

    private VrrVsyncCapConfigTweak Build(int frtc = -60, bool chill = false, int vsync = 1, bool enhancedSync = false)
    {
        _adlx.GetVsyncMode().Returns(Result<int>.Ok(vsync));          // 1 = coupée sauf avis du jeu
        _adlx.GetEnhancedSyncActif().Returns(Result<bool>.Ok(enhancedSync));
        _display.GetPrimaryGameDisplay()
            .Returns(Result<DisplayInfo>.Ok(new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144)));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.GetFrameRateTarget().Returns(Result<int>.Ok(frtc));   // éteint, cible réglée 60
        _adlx.GetChillActif().Returns(Result<bool>.Ok(chill));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new VrrVsyncCapConfigTweak(_nvApi, _display, _elevated, adlx: _adlx);
    }

    // Le combo entier, Vsync du pilote « toujours activée » puis FRTC ; l'inverse rend les deux, à l'envers.
    [Fact]
    public async Task Sur_AMD_le_combo_passe_par_la_Vsync_et_FRTC_et_l_inverse_rend_les_deux_a_l_envers()
    {
        var t = Build();
        t.AppliesTo(DesktopAmd(), State).Should().BeTrue();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        Received.InOrder(() =>
        {
            _elevated.ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 3), Arg.Any<CancellationToken>());
            _elevated.ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, 138), Arg.Any<CancellationToken>());
        });
        CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson).Value.Should().Equal(
            new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, -60),
            new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 1));
    }

    [Fact]
    public async Task Vsync_deja_toujours_activee_seul_FRTC_est_pose()
    {
        var r = await Build(vsync: 3).ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.Kind == AdlxTuningKind.VerticalRefresh), Arg.Any<CancellationToken>());
        CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson).Value.Should().ContainSingle()
            .Which.Should().Be(new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, -60));
    }

    [Fact]
    public async Task FRTC_refuse_la_Vsync_deja_posee_est_rendue()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o.GetType() == typeof(ApplyAdlxTuningOp) && ((ApplyAdlxTuningOp)o).Kind == AdlxTuningKind.FrameRateTarget), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "hors plage")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        await _elevated.Received(1).ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 1), Arg.Any<CancellationToken>());
    }

    // La seconde écriture refusée ET le retour arrière de la première refusé : la Vsync reste posée.
    // Le levier le dit par son code, pour que l'appelant garde le journal au lieu de croire que rien n'a changé.
    [Fact]
    public async Task Retour_arriere_rate_le_levier_dit_pose_en_partie()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()), Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "hors plage")),
                     Task.FromResult(Result.Fail(ErrorCodes.ElevationDeclined, "refusé")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
    }

    // Allumer la Vsync du pilote éteint Enhanced Sync, et la rendre ne le rallume pas.
    [Fact]
    public async Task Enhanced_Sync_allume_ou_illisible_ferme_le_levier()
    {
        Build(enhancedSync: true).AppliesTo(DesktopAmd(), State).Should().BeFalse();
        (await Build(enhancedSync: true).ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());

        var t = Build();
        _adlx.GetEnhancedSyncActif().Returns(Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu"));
        t.AppliesTo(DesktopAmd(), State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeTrue();
    }

    // L'aller-retour entier. Le snapshot que rend ApplyAsync doit se rendre par RevertAsync.
    [Fact]
    public async Task Le_snapshot_rendu_par_la_pose_se_rend_par_le_levier_lui_meme()
    {
        var t = Build();
        var pose = await t.ApplyAsync();

        var rendu = await t.RevertAsync(pose.Value!);

        rendu.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, -60), Arg.Any<CancellationToken>());
        await _elevated.Received(1).ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Deja_a_la_cible_ou_Chill_allume_ou_FRTC_illisible_rien_n_est_propose()
    {
        Build(frtc: 141, vsync: 3).AppliesTo(DesktopAmd(), State).Should().BeFalse();
        Build(frtc: 141).AppliesTo(DesktopAmd(), State).Should().BeTrue("la Vsync manque encore au combo");
        Build(chill: true).AppliesTo(DesktopAmd(), State).Should().BeFalse();

        var t = Build();
        _adlx.GetFrameRateTarget().Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu"));
        t.AppliesTo(DesktopAmd(), State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeTrue();
    }

    [Fact]
    public async Task Chill_allume_au_moment_de_poser_rien_n_est_ecrit()
    {
        var t = Build(chill: true);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
