using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// -----------------------------------------------------------------------
// SanePowerPlanTweak.
//
// Si le plan actif est Équilibré et qu'un bureau montre les symptômes de micro-saccades
// du parcage de cœurs, proposer Performances élevées via SetActivePowerPlanOp (en liste
// blanche, GUID de plan Windows connu). Capture d'abord le vrai GUID du plan d'origine
// pour que le retour arrière le restaure.
//
// Honnêteté : l'effet réel est « réduit les micro-saccades », jamais « +FPS ».
// Un test vérifie que la SummaryKey n'est pas une clé fps.
//
// Filtre : bureau seulement. Sur un portable, Équilibré peut être voulu pour la
// batterie → le levier se retire (ne s'applique pas).
//
// Tests unitaires purs avec des faux NSubstitute : aucun accès réel à l'API d'alimentation.
// -----------------------------------------------------------------------
public sealed class SanePowerPlanTweakTests
{
    private readonly IPowerPlanReader _power = Substitute.For<IPowerPlanReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    // Balanced sur un bureau, avec le GOULOT mesure par le scan.
    private static SettingsState Balanced(BottleneckState? goulot, IReadOnlyList<int>? parques = null) =>
        new SettingsState(
            TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
            GameMode: TriState.On, ParkedCoreIndexes: parques ?? Array.Empty<int>(), Vrr: TriState.On)
        with { GoulotMesure = goulot };

    private static SettingsState BalancedWithParking() => Balanced(BottleneckState.CpuBoundStrong, new[] { 6, 7 });

    private static SettingsState HighPerfWithParking() =>
        BalancedWithParking() with { PowerPlanGuid = KnownPowerPlans.HighPerformance.ToString() };

    private static HardwareInventory Hw(ChassisType chassis) => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        chassis);

    private SanePowerPlanTweak Build()
    {
        _power.GetActivePowerPlan().Returns(Result<Guid>.Ok(KnownPowerPlans.Balanced));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new SanePowerPlanTweak(_power, _elevated);
    }

    /// <summary>Sur un Ryzen X3D à deux blocs, le pilote de chipset AMD range le jeu sur le bloc à cache
    /// et parque l'autre, et il ne le fait que sous le plan Équilibré (constat convergent de la communauté AMD et des testeurs ;
    /// AMD n'en publie pas de note technique que nous ayons trouvée). Hautes performances casserait ce rangement : le levier
    /// reste fermé, même avec le processeur en goulot ; le routage est l'affaire de fixit.x3d_ccd.</summary>
    [Fact]
    public void X3D_a_deux_blocs_ne_recoit_jamais_Hautes_performances()
    {
        var hw = Hw(ChassisType.Desktop);
        var deuxBlocs = hw with { Cpu = hw.Cpu with { Model = "Ryzen 9 7950X3D", Cores = 16, Threads = 32, CcdCount = 2 } };

        Build().AppliesTo(deuxBlocs, Balanced(BottleneckState.CpuBoundStrong)).Should().BeFalse("le pilote AMD veut Équilibré");
        Build().AppliesTo(hw, Balanced(BottleneckState.CpuBoundStrong)).Should().BeTrue("un X3D à un bloc n'a rien à router");
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("fixit.power_plan");
        t.Category.Should().Be(TweakCategory.FixIt);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_dit_le_mecanisme_vrai_jamais_parking_stutter_ni_fps()
    {
        // Le parking est identique entre les deux plans (mesure du 10/09) ; ce que
        // le plan change, c'est la preference performance du processeur.
        var t = Build();
        t.SummaryKey.Should().Be("tweak.cpu.power_policy.raise_perf_preference");
        t.SummaryKey.Should().NotContainAny("fps", "parking", "stutter");
    }

    [Theory]
    [InlineData(BottleneckState.CpuBoundStrong)]
    [InlineData(BottleneckState.CpuBoundOrAppBound)]
    [InlineData(BottleneckState.MixedGpuLeaning)]
    public void AppliesTo_quand_le_processeur_limite_ce_jeu(BottleneckState goulot) =>
        Build().AppliesTo(Hw(ChassisType.Desktop), Balanced(goulot)).Should().BeTrue();

    [Fact]
    public void GPU_limitant_meme_avec_des_coeurs_parques_n_est_PAS_un_candidat() =>
        // L'ancien gate s'armait ici : parking lu, plan Equilibre. Sur un jeu GPU-bound, monter
        // la preference du CPU ne rend rien et coute du courant.
        Build().AppliesTo(Hw(ChassisType.Desktop), Balanced(BottleneckState.GpuBound, new[] { 6, 7 }))
            .Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData(BottleneckState.UnknownPartial)]
    public void Sans_goulot_MESURE_rien_ne_s_arme(BottleneckState? goulot) =>
        Build().AppliesTo(Hw(ChassisType.Desktop), Balanced(goulot, new[] { 6, 7 })).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_when_already_high_performance() =>
        Build().AppliesTo(Hw(ChassisType.Desktop), HighPerfWithParking()).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_laptop_balanced_may_be_wanted_for_battery() =>
        // Sur un portable, Équilibré peut être le choix du joueur pour la batterie → retrait.
        Build().AppliesTo(Hw(ChassisType.Laptop), BalancedWithParking()).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_handheld() =>
        Build().AppliesTo(Hw(ChassisType.Handheld), BalancedWithParking()).Should().BeFalse();

    [Fact]
    public async Task Apply_captures_original_plan_and_sets_high_performance_via_helper()
    {
        var t = Build();
        _power.GetActivePowerPlan().Returns(Result<Guid>.Ok(KnownPowerPlans.Balanced));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Bascule le plan actif sur Performances élevées (GUID connu, en liste blanche).
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetActivePowerPlanOp>(o => o.PlanGuid == KnownPowerPlans.HighPerformance),
            Arg.Any<CancellationToken>());
        // L'instantané porte l'origine capturée (Équilibré) en op inverse sérialisée.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<SetActivePowerPlanOp>()
            .Which.PlanGuid.Should().Be(KnownPowerPlans.Balanced);
    }

    [Fact]
    public async Task Apply_fails_clean_when_power_read_unavailable()
    {
        var t = Build();
        _power.GetActivePowerPlan().Returns(Result<Guid>.Fail(ErrorCodes.TweakSdkUnavailable, "no power api"));

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
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "switch failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _power.GetActivePowerPlan().Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_restores_the_captured_original_plan_via_helper()
    {
        var t = Build();
        var original = new SetActivePowerPlanOp(KnownPowerPlans.Balanced);
        var snap = new TweakSnapshot("fixit.power_plan", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetActivePowerPlanOp>(o => o.PlanGuid == KnownPowerPlans.Balanced),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("fixit.power_plan", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
