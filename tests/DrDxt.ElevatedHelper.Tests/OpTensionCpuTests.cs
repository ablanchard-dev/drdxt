using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

// L'op fermée du décalage de tension. La liste blanche ne laisse passer que 0 à −125 mV ; l'op traverse le codec
// à l'identique ; l'exécuteur la confie aux actions du processeur, et sans elles la refuse.
public sealed class OpTensionCpuTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(-50, true)]
    [InlineData(-125, true)]
    [InlineData(1, false)]
    [InlineData(-126, false)]
    public void La_liste_blanche_borne_le_decalage(int mv, bool admis)
        => new OpAllowlist().Validate(new SetCpuVoltageOffsetOp(mv)).IsOk.Should().Be(admis);

    [Fact]
    public void L_op_traverse_le_codec_a_l_identique()
    {
        var json = PrivilegedOpCodec.Serialize(new SetCpuVoltageOffsetOp(-50));
        json.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(json.Value!).Value.Should().Be(new SetCpuVoltageOffsetOp(-50));
    }

    private static PrivilegedOpExecutor Executeur(ICpuPowerActions? cpu) => new(
        Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
        Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
        Substitute.For<IADLXTuningActions>(), cpu: cpu);

    [Fact]
    public async Task L_executeur_la_confie_aux_actions_du_processeur_et_sans_elles_la_refuse()
    {
        var cpu = Substitute.For<ICpuPowerActions>();
        cpu.SetVoltageOffset(-50).Returns(Result.Ok());
        (await Executeur(cpu).ExecuteAsync(new SetCpuVoltageOffsetOp(-50), CancellationToken.None)).IsOk.Should().BeTrue();
        cpu.Received(1).SetVoltageOffset(-50);
        (await Executeur(null).ExecuteAsync(new SetCpuVoltageOffsetOp(-50), CancellationToken.None)).IsOk.Should().BeFalse();
    }
}
