using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests.Privileged;

// -----------------------------------------------------------------------
// R2 (lease watchdog) — the two NEW closed PrivilegedOp leaves that drive the
// helper-side dead-man's-switch:
//   * EstablishLeaseOp — registers a held/reversible tweak's REVERT op + an
//     expiry. The revert is carried as the codec-JSON of an existing typed op
//     (RevertOpJson) so the watchdog re-binds it through the same bounded codec
//     + allowlist at expiry — never a new generic privileged primitive.
//   * RenewLeaseOp — heartbeat that extends a lease's expiry.
//
// Both must round-trip through the bounded discriminant codec exactly like the
// other leaves; the closed leaf set grows by exactly these two members.
// -----------------------------------------------------------------------
public sealed class LeaseOpTests
{
    [Fact]
    public void EstablishLeaseOp_roundtrips_typed()
    {
        // The revert op is the existing typed inverse, carried as codec JSON.
        var revert = PrivilegedOpCodec.Serialize(
            new ApplyNvApiClockOffsetOp(GpuIndex: 0, CoreOffsetMhz: 0, MemoryOffsetMhz: 0)).Value!;
        var op = new EstablishLeaseOp("lease-1", revert, LeaseSeconds: 30);

        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue();

        var back = PrivilegedOpCodec.Deserialize(json.Value!);
        back.IsOk.Should().BeTrue();
        back.Value.Should().BeOfType<EstablishLeaseOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void RenewLeaseOp_roundtrips_typed()
    {
        var op = new RenewLeaseOp("lease-1", LeaseSeconds: 30);

        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue();

        var back = PrivilegedOpCodec.Deserialize(json.Value!);
        back.IsOk.Should().BeTrue();
        back.Value.Should().BeOfType<RenewLeaseOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void Closed_leaf_set_now_includes_the_two_lease_ops()
    {
        var names = Enum.GetNames<PrivilegedOpType>();
        names.Should().Contain(nameof(PrivilegedOpType.EstablishLease));
        names.Should().Contain(nameof(PrivilegedOpType.RenewLease));
    }
}
