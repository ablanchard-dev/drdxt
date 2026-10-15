using DrDxt.Core;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Pendant que l'inverse d'un bail expiré part, un nouveau bail du même nom (même réglage,
/// même un autre client, une App relancée) est refusé ; sinon son écriture pouvait passer avant l'inverse, qui la défaisait.
/// </summary>
public sealed class InverseEnCoursTests
{
    private static readonly string Inverse = PrivilegedOpCodec.Serialize(new ApplyNvApiClockOffsetOp(0, 0, 0)).Value!;

    [Fact]
    public async Task Un_nouveau_bail_du_meme_nom_attend_la_fin_de_l_inverse()
    {
        var maintenant = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var horloge = Substitute.For<IClock>();
        horloge.UtcNow.Returns(_ => maintenant);
        var inverseLibre = new TaskCompletionSource<Result>();
        var executeur = Substitute.For<IPrivilegedOpExecutor>();
        executeur.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(_ => inverseLibre.Task);
        var w = new LeaseWatchdog(horloge, new OpAllowlist(), executeur, Substitute.For<IDiagnosticsLog>());

        w.Establish(new LeaseOwner(ProcessId: 1), "gpu-expert", Inverse, 1).IsOk.Should().BeTrue();
        maintenant = maintenant.AddSeconds(5);
        var tic = w.TickAsync(CancellationToken.None); // l'inverse part et reste en cours

        w.Establish(new LeaseOwner(ProcessId: 2), "gpu-expert", Inverse, 120).IsOk.Should().BeFalse("l'inverse n'est pas fini");
        w.Establish(new LeaseOwner(ProcessId: 2), "gpu-ventilateur", Inverse, 120).IsOk.Should().BeTrue("un autre réglage passe");

        inverseLibre.SetResult(Result.Ok());
        await tic;
        w.Establish(new LeaseOwner(ProcessId: 2), "gpu-expert", Inverse, 120).IsOk.Should().BeTrue("l'inverse est fini");
    }

    // L'App plantée a laissé son bail ; l'App relancée (autre client) ne prend pas le même réglage avant qu'il soit rendu.
    [Fact]
    public async Task Un_bail_tenu_par_un_autre_client_bloque_le_meme_nom_jusqu_a_son_rendu()
    {
        var maintenant = new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero);
        var horloge = Substitute.For<IClock>();
        horloge.UtcNow.Returns(_ => maintenant);
        var executeur = Substitute.For<IPrivilegedOpExecutor>();
        executeur.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        var w = new LeaseWatchdog(horloge, new OpAllowlist(), executeur, Substitute.For<IDiagnosticsLog>());

        w.Establish(new LeaseOwner(ProcessId: 1), "gpu-expert", Inverse, 120).IsOk.Should().BeTrue();
        w.Establish(new LeaseOwner(ProcessId: 2), "gpu-expert", Inverse, 120).IsOk.Should().BeFalse("l'ancien client tient le réglage");
        w.Establish(new LeaseOwner(ProcessId: 1), "gpu-expert", Inverse, 120).IsOk.Should().BeTrue("le même client le rétablit");

        maintenant = maintenant.AddSeconds(130);
        await w.TickAsync(CancellationToken.None); // l'ancien bail expire et rend l'origine
        w.Establish(new LeaseOwner(ProcessId: 2), "gpu-expert", Inverse, 120).IsOk.Should().BeTrue("rendu : le nouveau passe");
    }
}
