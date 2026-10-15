using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests.Privileged;

// Couper ou réactiver un programme au démarrage, comme le Gestionnaire des tâches.
public sealed class SetStartupApprovalOpTests
{
    [Fact]
    public void L_op_fait_l_aller_retour_du_codec()
    {
        var op = new SetStartupApprovalOp(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "Discord", false);
        var back = PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(op).Value!);
        back.Value.Should().BeOfType<SetStartupApprovalOp>().And.Be(op);
    }

    [Fact]
    public void Actif_ecrit_02_suivi_de_zeros()
        => SetStartupApprovalOp.Octets(true, DateTime.UtcNow).Should().Equal(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Coupe_ecrit_03_et_l_heure_de_la_coupure()
    {
        var quand = new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
        var o = SetStartupApprovalOp.Octets(false, quand);
        o.Should().HaveCount(12);
        o[0].Should().Be(3);
        o[1..4].Should().Equal(0, 0, 0);
        BitConverter.ToInt64(o, 4).Should().Be(quand.ToFileTimeUtc());
    }
}
