using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// La température du processeur passe par une chaîne
// nommée ; la première source qui répond gagne, une source qui plante passe la main, rien de lu = « non lue ». Le test de
// composition casse si un étage de secours disparaît.
public sealed class TemperatureProcesseurTests
{
    private static readonly TemperatureHwinfo.Lecture A = new(70, "a"), B = new(80, "b");

    [Fact]
    public void La_premiere_source_qui_repond_gagne_et_une_source_qui_plante_passe_la_main()
    {
        TemperatureProcesseur.Premiere([() => A, () => B]).Should().Be(A);
        TemperatureProcesseur.Premiere([() => null, () => B]).Should().Be(B);
        TemperatureProcesseur.Premiere([() => throw new InvalidOperationException(), () => B]).Should().Be(B);
        TemperatureProcesseur.Premiere([() => null, () => null]).Should().BeNull("rien lu : « non lue », jamais une valeur inventée");
    }

    [Fact]
    public void La_chaine_garde_ses_etages_dans_l_ordre()
        => TemperatureProcesseur.Chaine.Select(c => c.Nom).Should()
            .Equal("HWiNFO", "PawnIO direct", "service DrDXT", "PawnIO AMD direct", "service DrDXT, AMD");
}
