using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests.Privileged;

// -----------------------------------------------------------------------
// Les deux feuilles PrivilegedOp fermées qui pilotent
// l'homme mort côté helper :
//   * EstablishLeaseOp : enregistre l'op de retour arrière d'un levier tenu et réversible + une
//     échéance. Le retour arrière est porté en JSON codec d'une op typée existante
//     (RevertOpJson) pour que le chien de garde le relie par le même codec borné
//     et la même liste blanche à l'échéance ; jamais une nouvelle primitive privilégiée générique.
//   * RenewLeaseOp : battement qui prolonge l'échéance d'un bail.
//
// Les deux doivent faire l'aller-retour par le codec à discriminant borné exactement comme les
// autres feuilles ; l'ensemble fermé de feuilles grandit d'exactement ces deux membres.
// -----------------------------------------------------------------------
public sealed class LeaseOpTests
{
    [Fact]
    public void EstablishLeaseOp_roundtrips_typed()
    {
        // L'op de retour arrière est l'inverse typé existant, porté en JSON codec.
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
