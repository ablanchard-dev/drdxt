using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

/// <summary>
/// Un essai Expert rendu gardait son bail jusqu'à l'expiration (2 min) ; le helper
/// restait ouvert après la fermeture de l'App. Rendu avec succès ⇒ le bail est libéré ; rendu raté ⇒ il reste, pour que le
/// helper rende l'origine seul.
/// </summary>
public sealed class EssaiRenduLibereLeBailTests
{
    private sealed class Client(Result rendu) : IElevatedClient
    {
        public List<PrivilegedOp> Ops { get; } = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Ops.Add(op);
            return Task.FromResult(op is RestoreGpuFanAutoOp ? rendu : Result.Ok());
        }
    }

    private static (EssaiVentilateur, Client) Essai(Result rendu)
    {
        var client = new Client(rendu);
        var essai = new EssaiVentilateur(client, Substitute.For<IRevertEnAttente>(), new RestoreGpuFanAutoOp(0),
            new HeartbeatSender(client, VentilateurExpert.IdBail, 120));
        return (essai, client);
    }

    [Fact]
    public async Task Rendu_le_bail_est_libere()
    {
        var (essai, client) = Essai(Result.Ok());
        (await essai.RendreAsync()).IsOk.Should().BeTrue();
        client.Ops.Should().ContainEquivalentOf(new RenewLeaseOp(VentilateurExpert.IdBail, 1));
    }

    [Fact]
    public async Task Rendu_rate_le_bail_reste()
    {
        var (essai, client) = Essai(Result.Fail(ErrorCodes.HelperOpFailed, "politique non relue"));
        (await essai.RendreAsync()).IsOk.Should().BeFalse();
        client.Ops.OfType<RenewLeaseOp>().Should().BeEmpty();
    }

    // Le bail expiré (veille de plus de deux minutes), le helper a rendu l'origine : le journal restait, et l'Expert comme
    // l'autopilote se croyaient « encore en essai » jusqu'au redémarrage de l'App.
    private sealed class BailInconnu(string code = ErrorCodes.LeaseReverted) : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) => Task.FromResult(op is RenewLeaseOp
            ? Result.Fail(code, "refus du bail (test)")
            : Result.Ok());
    }

    // Helper relancé : son registre est vide, le bail « inconnu » ; le décalage est peut-être encore posé. Le journal
    // reste, pour que le démarrage suivant rende la carte.
    [Fact]
    public async Task Un_helper_relance_ne_leve_pas_le_journal()
    {
        var client = new BailInconnu(ErrorCodes.LeaseUnknown);
        var journal = Substitute.For<IRevertEnAttente>();
        var bail = new HeartbeatSender(client, ReglageCarteExpert.IdBail, 120);
        await using var essai = new EssaiCarte(null!, journal, new TweakSnapshot("gpu.undervolt", "{}", "{}"), bail);

        await bail.PulseAsync(CancellationToken.None);

        journal.DidNotReceive().Lever();
    }

    [Fact]
    public async Task Bail_perdu_le_journal_du_ventilateur_est_leve()
    {
        var client = new BailInconnu();
        var journal = Substitute.For<IRevertEnAttente>();
        var bail = new HeartbeatSender(client, VentilateurExpert.IdBail, 120);
        await using var essai = new EssaiVentilateur(client, journal, new RestoreGpuFanAutoOp(0), bail);

        await bail.PulseAsync(CancellationToken.None);

        journal.Received(1).Lever();
    }

    [Fact]
    public async Task Bail_perdu_le_journal_de_la_carte_est_leve()
    {
        var client = new BailInconnu();
        var journal = Substitute.For<IRevertEnAttente>();
        var bail = new HeartbeatSender(client, ReglageCarteExpert.IdBail, 120);
        await using var essai = new EssaiCarte(null!, journal, new TweakSnapshot("gpu.undervolt", "{}", "{}"), bail);

        await bail.PulseAsync(CancellationToken.None);

        journal.Received(1).Lever();
    }

    // Une pose ratée que le helper a déjà rendue laissait le bail vivre 120 s : il rejouait ensuite l'origine par-dessus un
    // réglage posé entre-temps. Rendue, le bail est libéré ; partielle (rendu raté), il reste pour que le helper rende seul.
    private sealed class PoseRefusee(string code) : IElevatedClient
    {
        public List<PrivilegedOp> Ops { get; } = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Ops.Add(op);
            return Task.FromResult(op is ApplyGpuFanCurveOp ? Result.Fail(code, "refus (test)") : Result.Ok());
        }
    }

    private static IRevertEnAttente JournalOk()
    {
        var j = Substitute.For<IRevertEnAttente>();
        j.Poser(default!, default).ReturnsForAnyArgs(Result.Ok());
        return j;
    }

    [Fact]
    public async Task Pose_ratee_et_rendue_le_bail_est_libere()
    {
        var client = new PoseRefusee(ErrorCodes.HelperOpFailed);
        (await new VentilateurExpert(client, JournalOk()).PoserAsync(60)).IsOk.Should().BeFalse();
        client.Ops.Should().ContainEquivalentOf(new RenewLeaseOp(VentilateurExpert.IdBail, 1));
    }

    [Fact]
    public async Task Pose_partielle_le_bail_reste()
    {
        var client = new PoseRefusee(ErrorCodes.TweakPartiallyApplied);
        (await new VentilateurExpert(client, JournalOk()).PoserAsync(60)).IsOk.Should().BeFalse();
        client.Ops.OfType<RenewLeaseOp>().Should().NotContain(r => r.LeaseSeconds == 1);
    }
}
