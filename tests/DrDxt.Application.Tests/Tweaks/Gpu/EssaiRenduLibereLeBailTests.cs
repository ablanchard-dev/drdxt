using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

/// <summary>
/// Autocontrôle du 08/10/2026 (SEC1) : un essai Expert rendu gardait son bail jusqu'à l'expiration (2 min) ; le helper
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
}
