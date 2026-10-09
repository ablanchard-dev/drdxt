using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D8.8, revue n° 3) : le tissu du réseau gardait du violet, du magenta et de l'ambre alors que
// l'interface est passée à la famille cyan. Aucune teinte où le rouge domine le vert : ni magenta, ni violet, ni ambre.
public sealed class ReseauSansMagentaTests
{
    [Fact]
    public void Les_teintes_du_reseau_restent_dans_la_famille_cyan_et_acier()
    {
        ReseauNeuronal.Teintes.Should().NotBeEmpty("témoin : la palette est lue");
        ReseauNeuronal.Teintes.Should().OnlyContain(c => c.G >= c.R && c.B >= c.R, "le rouge ne domine jamais : pas de magenta, de violet ni d'ambre");
    }
}
