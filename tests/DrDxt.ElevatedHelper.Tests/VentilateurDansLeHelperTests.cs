using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>L'aiguillage du ventilateur dans le helper. Un seul point = une vitesse fixe ; une courbe à
/// plusieurs points reste refusée ; le retour à l'automatique a son op, admise par la liste blanche.</summary>
public sealed class VentilateurDansLeHelperTests
{
    private readonly IVentilateurActions _ventilateur = Substitute.For<IVentilateurActions>();

    private PrivilegedOpExecutor Executeur(bool avecVentilateur = true) => new(
        Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
        Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
        Substitute.For<IADLXTuningActions>(), ventilateur: avecVentilateur ? _ventilateur : null);

    [Fact]
    public async Task Un_seul_point_pose_une_vitesse_fixe()
    {
        _ventilateur.Poser(0, 70).Returns(Result.Ok());
        (await Executeur().ExecuteAsync(new ApplyGpuFanCurveOp(0, [new FanCurvePointDto(0, 70)]), default)).IsOk.Should().BeTrue();
        _ventilateur.Received(1).Poser(0, 70);
    }

    [Fact]
    public async Task Une_courbe_a_plusieurs_points_reste_refusee()
    {
        var r = await Executeur().ExecuteAsync(new ApplyGpuFanCurveOp(0, [new FanCurvePointDto(40, 40), new FanCurvePointDto(80, 70)]), default);
        r.IsOk.Should().BeFalse();
        _ventilateur.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Le_retour_a_l_automatique_passe_par_rendre()
    {
        _ventilateur.Rendre(0).Returns(Result.Ok());
        (await Executeur().ExecuteAsync(new RestoreGpuFanAutoOp(0), default)).IsOk.Should().BeTrue();
        _ventilateur.Received(1).Rendre(0);
    }

    [Fact]
    public async Task Sans_ventilateur_branche_tout_reste_refuse()
    {
        (await Executeur(avecVentilateur: false).ExecuteAsync(new RestoreGpuFanAutoOp(0), default)).IsOk.Should().BeFalse();
        (await Executeur(avecVentilateur: false).ExecuteAsync(new ApplyGpuFanCurveOp(0, [new FanCurvePointDto(0, 70)]), default)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void La_liste_blanche_admet_le_retour_et_refuse_un_index_negatif()
    {
        new OpAllowlist().Validate(new RestoreGpuFanAutoOp(0)).IsOk.Should().BeTrue();
        new OpAllowlist().Validate(new RestoreGpuFanAutoOp(-1)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Le_retour_traverse_le_fil()
    {
        var op = new RestoreGpuFanAutoOp(1);
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(op).Value!).Value.Should().Be(op);
    }
}
