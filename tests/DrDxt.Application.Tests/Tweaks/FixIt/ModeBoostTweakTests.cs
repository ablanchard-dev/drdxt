using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// L-CPU (07/10/2026) : le boost du processeur coupé (PERFBOOSTMODE secteur = 0) rendu au défaut de son propre plan.
// Doublures NSubstitute seulement : aucune lecture ni écriture de la vraie machine.
public sealed class ModeBoostTweakTests
{
    private static readonly Guid Plan = KnownPowerPlans.Balanced;
    private readonly IPowerPlanReader _power = Substitute.For<IPowerPlanReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static SettingsState Etat(ModeDeBoostLu? boost) =>
        new SettingsState(TriState.On, TriState.On, Plan.ToString(), TriState.On,
            GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On) with { ModeDeBoost = boost };

    private static HardwareInventory Hw(ChassisType chassis, string vendeur = "Intel") => new(
        new CpuInfo(vendeur == "AMD" ? "Ryzen 5 5600" : "Core i7-14700F", vendeur, 8, 16, false, 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.00", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3200, 3200) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        chassis);

    private ModeBoostTweak Build(uint valeurLue = 0, uint defaut = 2)
    {
        _power.GetActivePowerPlan().Returns(Result<Guid>.Ok(Plan));
        _power.LireModeDeBoost(Plan).Returns(Result<ModeDeBoostLu>.Ok(new ModeDeBoostLu(Plan, valeurLue, defaut)));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new ModeBoostTweak(_power, _elevated);
    }

    [Theory]
    [InlineData("Intel")]
    [InlineData("AMD")]
    public void Boost_coupe_sur_un_fixe_Intel_ou_AMD_est_propose(string vendeur) =>
        Build().AppliesTo(Hw(ChassisType.Desktop, vendeur), Etat(new ModeDeBoostLu(Plan, 0, 2))).Should().BeTrue();

    // LAP1 (08/10/2026) : le levier n'écrit que la valeur « sur secteur » ; la batterie n'est jamais touchée.
    [Fact]
    public void Sur_un_portable_le_boost_secteur_est_propose() =>
        Build().AppliesTo(Hw(ChassisType.Laptop), Etat(new ModeDeBoostLu(Plan, 0, 2))).Should().BeTrue();

    [Fact]
    public void Jamais_sur_une_console_portable() =>
        Build().AppliesTo(Hw(ChassisType.Handheld), Etat(new ModeDeBoostLu(Plan, 0, 2))).Should().BeFalse();

    [Theory]
    [InlineData(2u, 2u)]   // le cas des cinq machines enregistrées : rien à rendre
    [InlineData(1u, 2u)]   // boost allumé, autre que le défaut : pas notre affaire
    [InlineData(0u, 0u)]   // un plan dont le défaut est « coupé » (Économie) : c'est son réglage
    [InlineData(0u, 5u)]   // un défaut hors de ce que Microsoft documente : jamais écrit
    public void Rien_quand_le_boost_n_est_pas_coupe_contre_son_plan(uint valeur, uint defaut) =>
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(new ModeDeBoostLu(Plan, valeur, defaut))).Should().BeFalse();

    [Fact]
    public void Non_lu_ne_propose_rien_et_le_dit()
    {
        var t = Build();
        t.AppliesTo(Hw(ChassisType.Desktop), Etat(null)).Should().BeFalse();
        t.EtatNonLu(Etat(null)).Should().BeTrue();
        t.EtatNonLu(Etat(new ModeDeBoostLu(Plan, 2, 2))).Should().BeFalse();
    }

    [Fact]
    public void Un_plan_lu_ailleurs_que_le_plan_actif_ne_propose_rien() =>
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(new ModeDeBoostLu(KnownPowerPlans.HighPerformance, 0, 2)))
            .Should().BeFalse();

    [Fact]
    public async Task Applique_le_defaut_du_plan_et_garde_l_original_pour_l_annulation()
    {
        var t = Build(valeurLue: 0, defaut: 3);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(new SetProcessorBoostModeOp(Plan, 3), Arg.Any<CancellationToken>());
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value.Should().Be(new SetProcessorBoostModeOp(Plan, 0));
        PrivilegedOpCodec.Deserialize(r.Value.AppliedValueJson).Value.Should().Be(new SetProcessorBoostModeOp(Plan, 3));

        (await t.RevertAsync(r.Value)).IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(new SetProcessorBoostModeOp(Plan, 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relu_juste_avant_et_deja_rendu_par_quelqu_un_rien_n_est_ecrit()
    {
        var t = Build(valeurLue: 2, defaut: 2);

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lecture_ratee_rien_n_est_ecrit()
    {
        var t = Build();
        _power.LireModeDeBoost(Plan).Returns(Result<ModeDeBoostLu>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu"));

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Se_mesure_en_jeu_et_ne_promet_pas_d_images()
    {
        var t = Build();
        ((ITweak)t).TakesEffectAfterReboot.Should().BeFalse();
        t.SummaryKey.Should().Be("tweak.fixit.mode_boost.summary");
        CatalogueDeCandidats.Decrire(t.Id)!.Kind.Should().Be(CandidateKind.AutoHot);
        CatalogueDeCandidats.Decrire(t.Id)!.Surface.Should().Be(SurfaceTouchee.Processeur);
        CandidateSelector.FermeeParLesArrets(SurfaceTouchee.Processeur).Should().BeTrue();
    }
}
