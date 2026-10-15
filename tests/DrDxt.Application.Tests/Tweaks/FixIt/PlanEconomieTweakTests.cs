using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// Le plan « Économie d'énergie » rendu au plan Équilibré sur un PC de bureau. Doublures NSubstitute seulement :
// aucune lecture ni écriture de la vraie machine.
public sealed class PlanEconomieTweakTests
{
    private readonly IPowerPlanReader _power = Substitute.For<IPowerPlanReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static SettingsState Etat(string? plan) =>
        new SettingsState(TriState.On, TriState.On, plan!, TriState.On,
            GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Hw(ChassisType chassis) => new(
        new CpuInfo("Core i5-12400F", "GenuineIntel", 6, 12, false, 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 3060", "NVIDIA", 12L << 30, "560.00", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3200, 3200) },
        new MotherboardInfo("MSI", "B660"), new BiosInfo("1.0", null, true), chassis);

    private PlanEconomieTweak Build(Guid lu)
    {
        _power.GetActivePowerPlan().Returns(Result<Guid>.Ok(lu));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new PlanEconomieTweak(_power, _elevated);
    }

    [Fact]
    public void Sur_un_pc_de_bureau_en_economie_c_est_propose()
        => Build(KnownPowerPlans.PowerSaver).AppliesTo(Hw(ChassisType.Desktop), Etat(KnownPowerPlans.PowerSaver.ToString())).Should().BeTrue();

    [Theory]
    [InlineData(ChassisType.Laptop)]
    [InlineData(ChassisType.Handheld)]
    public void Jamais_sur_un_portable_ou_on_economise_peut_etre_expres(ChassisType chassis)
        => Build(KnownPowerPlans.PowerSaver).AppliesTo(Hw(chassis), Etat(KnownPowerPlans.PowerSaver.ToString())).Should().BeFalse();

    [Fact]
    public void Un_autre_plan_rien()
        => Build(KnownPowerPlans.Balanced).AppliesTo(Hw(ChassisType.Desktop), Etat(KnownPowerPlans.Balanced.ToString())).Should().BeFalse();

    [Fact]
    public void Non_lu_est_dit_non_lu()
    {
        var t = Build(KnownPowerPlans.PowerSaver);
        t.AppliesTo(Hw(ChassisType.Desktop), Etat(null)).Should().BeFalse();
        t.EtatNonLu(Etat(null)).Should().BeTrue();
        t.EtatNonLu(Etat(KnownPowerPlans.Balanced.ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_passe_en_equilibre_et_annuler_rend_l_economie()
    {
        var t = Build(KnownPowerPlans.PowerSaver);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        await _elevated.Received(1).ExecuteAsync(new SetActivePowerPlanOp(KnownPowerPlans.Balanced), Arg.Any<CancellationToken>());
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value.Should().Be(new SetActivePowerPlanOp(KnownPowerPlans.PowerSaver));
        PrivilegedOpCodec.Deserialize(r.Value.AppliedValueJson).Value.Should().Be(new SetActivePowerPlanOp(KnownPowerPlans.Balanced));

        await t.RevertAsync(r.Value);
        await _elevated.Received(1).ExecuteAsync(new SetActivePowerPlanOp(KnownPowerPlans.PowerSaver), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relu_hors_economie_juste_avant_rien_n_est_ecrit()
    {
        (await Build(KnownPowerPlans.HighPerformance).ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Le_levier_est_un_reglage_pose_et_relu()
    {
        ((ITweak)Build(KnownPowerPlans.PowerSaver)).TakesEffectAfterReboot.Should().BeFalse();
        CatalogueDeCandidats.Decrire("fixit.plan_economie")!.Kind.Should().Be(CandidateKind.AutoReglage);
    }
}
