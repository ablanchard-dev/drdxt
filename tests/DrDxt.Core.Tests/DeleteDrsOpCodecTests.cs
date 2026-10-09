using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>PHASE 18 F1 lot 3 — l'inverse d'une ecriture au profil d'un jeu doit TRAVERSER le fil.
/// Sans valeur d'enum et sans ses deux bras de codec, l'op serait refusee hors du processus :
/// le revert marcherait en test et jamais en production (le defaut paye le 26/08 par le BIOS).</summary>
public class DeleteDrsOpCodecTests
{
    [Fact]
    public void La_suppression_DRS_traverse_le_fil_a_l_identique()
    {
        var op = new DeleteNvApiDrsSettingOp(0x1057EB71, @"C:\Games\cod.exe",
            DrsValueOrigin.InheritedFromGlobalProfile, 1);

        var fil = PrivilegedOpCodec.Serialize(op);
        fil.IsOk.Should().BeTrue();
        fil.Value.Should().Contain("\"op\":18", "la valeur est appendue, jamais renumerotee");

        var relu = PrivilegedOpCodec.Deserialize(fil.Value);
        relu.IsOk.Should().BeTrue();
        relu.Value.Should().Be(op);
    }
}
