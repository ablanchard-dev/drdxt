using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// Le chemin du décalage tenu peut armer l'homme mort du helper.
//
// Une fois un décalage GPU tenu appliqué, le levier peut établir un bail dont le retour
// arrière enregistré est la même op inverse qu'il calcule déjà pour RevertAsync, et recevoir
// un HeartbeatSender démarré qu'il garde en vie pendant la tenue. Si le contrôleur meurt,
// le battement s'arrête et le helper revient en arrière tout seul.
//
// Tout passe par les abstractions du cœur (IElevatedClient) : le levier ne référence jamais
// le helper. Un client enregistreur capture exactement ce qui traverse la frontière.
// -----------------------------------------------------------------------
public sealed class GpuHeldOffsetLeaseTests
{
    /// <summary>Capture chaque op que le levier envoie à travers la frontière élevée.</summary>
    private sealed class RecordingClient : IElevatedClient
    {
        public List<PrivilegedOp> Sent { get; } = new();
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Sent.Add(op);
            return Task.FromResult(Result.Ok());
        }
    }

    // Un GpuTweakBase concret, juste pour atteindre l'aide protégée au bail.
    private sealed class ProbeTweak : GpuTweakBase
    {
        public ProbeTweak(IElevatedClient elevated) : base(elevated) { }
        public override string Id => "gpu.probe";
        public override string SummaryKey => "tweak.gpu.probe";
        public override bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public override Task<Result<TweakSnapshot>> ApplyAsync() =>
            Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "probe"));

        // Expose l'aide protégée pour le test.
        public Task<Result<HeartbeatSender>> EstablishLeaseForTest(PrivilegedOp inverse, string leaseId, int seconds)
            => EstablishAutoRevertLeaseAsync(inverse, leaseId, seconds);
    }

    [Fact]
    public async Task Held_offset_establishes_a_lease_carrying_the_inverse_op()
    {
        var client = new RecordingClient();
        var tweak = new ProbeTweak(client);

        // L'inverse que le chemin du décalage tenu calcule déjà (rendre les horloges d'origine).
        var inverse = new ApplyNvApiClockOffsetOp(0, 0, 0);

        var result = await tweak.EstablishLeaseForTest(inverse, "gpu.undervolt:0", seconds: 30);

        result.IsOk.Should().BeTrue();
        await using var hb = result.Value!;

        // Exactement une EstablishLeaseOp a traversé la frontière, portant l'inverse en JSON
        // codec (reliable par le même codec borné côté helper).
        client.Sent.Should().ContainSingle().Which.Should().BeOfType<EstablishLeaseOp>();
        var est = (EstablishLeaseOp)client.Sent[0];
        est.LeaseId.Should().Be("gpu.undervolt:0");
        est.LeaseSeconds.Should().Be(30);

        var back = PrivilegedOpCodec.Deserialize(est.RevertOpJson);
        back.IsOk.Should().BeTrue();
        back.Value.Should().BeEquivalentTo(inverse);
    }

    [Fact]
    public async Task A_started_heartbeat_renews_the_lease()
    {
        var client = new RecordingClient();
        var tweak = new ProbeTweak(client);

        var result = await tweak.EstablishLeaseForTest(new ApplyNvApiClockOffsetOp(0, 0, 0), "l", 30);
        result.IsOk.Should().BeTrue();
        await using var hb = result.Value!;

        await hb.PulseAsync(default);

        client.Sent.OfType<RenewLeaseOp>().Should().ContainSingle()
            .Which.LeaseId.Should().Be("l");
    }

    [Fact]
    public async Task A_failed_establish_returns_fail_and_no_heartbeat()
    {
        // Un client qui refuse l'établissement (aucun helper en route, par exemple).
        var client = new FailingClient();
        var tweak = new ProbeTweak(client);

        var result = await tweak.EstablishLeaseForTest(new ApplyNvApiClockOffsetOp(0, 0, 0), "l", 30);

        result.IsOk.Should().BeFalse("if the lease cannot be established, there is no heartbeat to hand back");
    }

    private sealed class FailingClient : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "no helper"));
    }
}
