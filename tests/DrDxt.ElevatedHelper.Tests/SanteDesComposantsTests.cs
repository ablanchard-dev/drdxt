using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>DISM /CheckHealth, lecture. La phrase « repairable » est celle relevee sur la
/// machine de reference le jour meme ; une sortie inconnue n'est jamais devinee.</summary>
public class SanteDesComposantsTests
{
    [Theory]
    [InlineData("Image Version: 10.0.26200.9457\r\n\r\nNo component store corruption detected.\r\nThe operation completed successfully.", SanteDesComposants.Sain)]
    [InlineData("Image Version: 10.0.26200.9457\r\n\r\nThe component store is repairable.\r\nThe operation completed successfully.", SanteDesComposants.Reparable)]
    [InlineData("The component store cannot be repaired.", SanteDesComposants.NonReparable)]
    public void La_phrase_de_DISM_donne_l_etat(string sortie, string attendu)
        => SanteDesComposants.Interpreter(sortie).Should().Be(attendu);

    [Fact]
    public void Une_sortie_inconnue_n_est_pas_devinee()
        => SanteDesComposants.Interpreter("Error: 740 Elevated permissions are required").Should().BeNull();

    [Fact]
    public void L_op_traverse_le_codec_et_passe_l_allowlist()
    {
        var json = PrivilegedOpCodec.Serialize(new ReadComponentStoreHealthOp());
        json.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(json.Value!).Value.Should().BeOfType<ReadComponentStoreHealthOp>();
        new OpAllowlist().Validate(new ReadComponentStoreHealthOp()).IsOk.Should().BeTrue();
    }
}
