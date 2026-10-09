using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

public class UploadCapOpCodecTests
{
    [Fact]
    public void Le_plafond_d_envoi_et_son_inverse_traversent_le_fil_a_l_identique()
    {
        var pose = new ApplyUploadCapOp("cod.exe", 400_000_000);
        var fil = PrivilegedOpCodec.Serialize(pose);
        fil.Value.Should().Contain("\"op\":20", "la valeur est appendue, jamais renumerotee");
        PrivilegedOpCodec.Deserialize(fil.Value).Value.Should().Be(pose);

        var retire = PrivilegedOpCodec.Serialize(new RemoveUploadCapOp());
        retire.Value.Should().Contain("\"op\":21");
        PrivilegedOpCodec.Deserialize(retire.Value).Value.Should().BeOfType<RemoveUploadCapOp>();
    }

    [Fact]
    public void Les_ops_BCD_traversent_le_fil_a_l_identique()
    {
        var retrait = new DeleteBcdValueOp("bootstatuspolicy");
        var fil = PrivilegedOpCodec.Serialize(retrait);
        fil.Value.Should().Contain("\"op\":22");
        PrivilegedOpCodec.Deserialize(fil.Value).Value.Should().Be(retrait);
        var inverse = new SetBcdValueOp("bootstatuspolicy", "IgnoreAllFailures");
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(inverse).Value).Value.Should().Be(inverse);
    }
}
