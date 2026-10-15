using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

public class MicrocodeVminTests
{
    private const string RaptorLake = "Intel64 Family 6 Model 183 Stepping 1";

    [Fact]
    public void Un_Core_de_bureau_13e_14e_Raptor_Lake_se_juge_sur_sa_revision()
    {
        MicrocodeVmin.Perime("Intel(R) Core(TM) i7-14700F", 0x12B, RaptorLake).Should().BeTrue();
        MicrocodeVmin.Perime("Intel(R) Core(TM) i7-14700F", 0x133, RaptorLake).Should().BeFalse("relevé sur la machine de référence");
    }

    [Fact]
    public void Hors_Raptor_Lake_portable_ou_AMD_rien()
    {
        MicrocodeVmin.Perime("Intel(R) Core(TM) i5-13400F", 0x30, "Intel64 Family 6 Model 151 Stepping 5").Should().BeNull("puce Alder Lake");
        MicrocodeVmin.Perime("Intel(R) Core(TM) i7-14700HX", 0x100, RaptorLake).Should().BeNull("portable");
        MicrocodeVmin.Perime("AMD Ryzen 7 7800X3D", 0x100, RaptorLake).Should().BeNull();
        MicrocodeVmin.Perime("Intel(R) Core(TM) i7-14700F", null, RaptorLake).Should().BeTrue("concerné, révision non lue : jamais « pas concerné »");
    }

    // « Update Revision » se décodait à deux endroits (App et helper), à tenir en phase pour la garde
    // Vmin. Un seul décodage. 8 octets : la révision dans les 4 de poids fort ; 4 octets : la révision seule (selon Windows).
    [Fact]
    public void La_revision_se_decode_d_un_seul_endroit()
    {
        MicrocodeVmin.Revision([0, 0, 0, 0, 0x33, 0x01, 0, 0]).Should().Be(0x133u);
        MicrocodeVmin.Revision([0x2B, 0x01, 0, 0]).Should().Be(0x12Bu);
        MicrocodeVmin.Revision([1, 2]).Should().BeNull();
        MicrocodeVmin.Revision(null).Should().BeNull();
    }
}
