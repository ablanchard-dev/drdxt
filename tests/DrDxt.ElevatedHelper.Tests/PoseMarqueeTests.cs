using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

// Une pose marquée (journal d'écriture anticipée) et son acquit ne valent que ce que vaut l'op qu'ils portent, et ne se
// relisent jamais d'un fichier : ni charge de bail, ni origine du manifeste.
public sealed class PoseMarqueeTests
{
    private static readonly SetRegistryValueOp Hags =
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2");
    private static string Json(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;

    [Fact]
    public void Une_pose_marquee_vaut_ce_que_vaut_son_op()
    {
        var allowlist = new OpAllowlist();
        allowlist.Validate(new PoseEnAttenteDOrigineOp(Json(Hags))).IsOk.Should().BeTrue();
        allowlist.Validate(new AcquitterEcritureOp(Json(Hags))).IsOk.Should().BeTrue();

        var horsListe = Hags with { Path = @"SYSTEM\CurrentControlSet\Services\Tcpip" };
        allowlist.Validate(new PoseEnAttenteDOrigineOp(Json(horsListe))).IsOk.Should().BeFalse();
        allowlist.Validate(new AcquitterEcritureOp(Json(horsListe))).IsOk.Should().BeFalse();
        allowlist.Validate(new PoseEnAttenteDOrigineOp("{ pas une op")).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Une_pose_marquee_ne_porte_jamais_une_autre_op_du_journal()
    {
        var allowlist = new OpAllowlist();
        var marquee = new PoseEnAttenteDOrigineOp(Json(Hags));

        allowlist.Validate(new PoseEnAttenteDOrigineOp(Json(marquee))).IsOk.Should().BeFalse();
        allowlist.Validate(new PoseEnAttenteDOrigineOp(Json(new AcquitterEcritureOp(Json(Hags))))).IsOk.Should().BeFalse();
        allowlist.Validate(new AcquitterEcritureOp(Json(marquee))).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Ni_une_pose_marquee_ni_un_acquit_ne_sont_un_retour_de_bail()
    {
        var horloge = Substitute.For<IClock>();
        horloge.UtcNow.Returns(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        var gardien = new LeaseWatchdog(horloge, new OpAllowlist(), Substitute.For<IPrivilegedOpExecutor>(), Substitute.For<IDiagnosticsLog>());
        var proprietaire = new LeaseOwner(ProcessId: 4242);

        gardien.Establish(proprietaire, "temoin", Json(Hags), 30).IsOk.Should().BeTrue("témoin : le retour nu est admis");
        gardien.Establish(proprietaire, "pose", Json(new PoseEnAttenteDOrigineOp(Json(Hags))), 30).IsOk.Should().BeFalse();
        gardien.Establish(proprietaire, "acquit", Json(new AcquitterEcritureOp(Json(Hags))), 30).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Ni_une_pose_marquee_ni_un_acquit_ne_sont_une_origine_du_manifeste()
    {
        var allowlist = new OpAllowlist();
        var t = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        CommitManifestOp Avec(string origine)
            => new([], [new AppliedTweak("latency.hagsOn", Json(Hags), origine, t)]);

        allowlist.Validate(Avec(Json(Hags with { Value = "1" }))).IsOk.Should().BeTrue("témoin : une origine nue est admise");
        allowlist.Validate(Avec(Json(new PoseEnAttenteDOrigineOp(Json(Hags))))).IsOk.Should().BeFalse();
        allowlist.Validate(Avec(Json(new AcquitterEcritureOp(Json(Hags))))).IsOk.Should().BeFalse();
    }

    // Sans journal devant lui (racine %ProgramData%\DrDxt non fiable au lancement du helper), une pose marquée partait nue :
    // l'écriture que le journal devait couvrir s'exécutait sans filet, et l'App promettait un rendu qui n'existait pas. Elle
    // est refusée, rien n'est écrit ; un acquit n'a toujours rien à faire.
    [Fact]
    public async Task L_executeur_sans_journal_refuse_une_pose_marquee_et_l_acquit_ne_fait_rien()
    {
        var registre = Substitute.For<IRegistryActions>();
        registre.SetValue(default, default!, default!, default, default!).ReturnsForAnyArgs(Result.Ok());
        var executeur = new PrivilegedOpExecutor(registre, Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
            Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
            Substitute.For<IADLXTuningActions>());

        (await executeur.ExecuteAsync(new PoseEnAttenteDOrigineOp(Json(Hags)), CancellationToken.None)).Error?.Code
            .Should().Be(ErrorCodes.JournalIndisponible);
        registre.ReceivedCalls().Should().BeEmpty();

        (await executeur.ExecuteAsync(new AcquitterEcritureOp(Json(Hags)), CancellationToken.None)).IsOk.Should().BeTrue();
        registre.ReceivedCalls().Should().BeEmpty();
    }
}
