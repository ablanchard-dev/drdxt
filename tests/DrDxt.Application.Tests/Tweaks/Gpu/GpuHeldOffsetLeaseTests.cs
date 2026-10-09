using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// R2 — the HELD-OFFSET path can arm the helper's dead-man's-switch.
//
// After a held GPU offset is applied, the tweak can establish a lease whose
// recorded revert is the same inverse op it already computes for RevertAsync, and
// get back a started HeartbeatSender it keeps alive for the held duration. If the
// controller dies, the heartbeat stops and the helper auto-reverts.
//
// All through Core abstractions (IElevatedClient) — the tweak never references the
// helper. A recording client captures exactly what crosses the boundary.
// -----------------------------------------------------------------------
public sealed class GpuHeldOffsetLeaseTests
{
    /// <summary>Captures every op the tweak sends through the elevated boundary.</summary>
    private sealed class RecordingClient : IElevatedClient
    {
        public List<PrivilegedOp> Sent { get; } = new();
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Sent.Add(op);
            return Task.FromResult(Result.Ok());
        }
    }

    // A concrete GpuTweakBase just to reach the protected lease helper.
    private sealed class ProbeTweak : GpuTweakBase
    {
        public ProbeTweak(IElevatedClient elevated) : base(elevated) { }
        public override string Id => "gpu.probe";
        public override string SummaryKey => "tweak.gpu.probe";
        public override bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public override Task<Result<TweakSnapshot>> ApplyAsync() =>
            Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "probe"));

        // Expose the protected helper for the test.
        public Task<Result<HeartbeatSender>> EstablishLeaseForTest(PrivilegedOp inverse, string leaseId, int seconds)
            => EstablishAutoRevertLeaseAsync(inverse, leaseId, seconds);
    }

    [Fact]
    public async Task Held_offset_establishes_a_lease_carrying_the_inverse_op()
    {
        var client = new RecordingClient();
        var tweak = new ProbeTweak(client);

        // The inverse the held-offset path already computes (restore stock clocks).
        var inverse = new ApplyNvApiClockOffsetOp(0, 0, 0);

        var result = await tweak.EstablishLeaseForTest(inverse, "gpu.undervolt:0", seconds: 30);

        result.IsOk.Should().BeTrue();
        await using var hb = result.Value!;

        // Exactly one EstablishLeaseOp crossed the boundary, carrying the inverse as
        // codec JSON (re-bindable through the same bounded codec on the helper side).
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
        // A client that refuses the establish (e.g. no helper running).
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
