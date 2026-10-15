using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>Une image de chargement de plusieurs secondes fait sauter des numéros de seconde : la coupure du lancement
/// se fait sur la seconde réelle, pas sur le rang du seau, sinon une seconde de chargement restait gardée.</summary>
public class FrameSeriesHygieneLongueImageTests
{
    [Fact]
    public void Une_image_de_trois_secondes_ne_fait_pas_garder_la_suite_du_chargement()
    {
        var frames = new List<double> { 3000 };
        frames.AddRange(Enumerable.Repeat(50.0, 20));
        frames.AddRange(Enumerable.Repeat(10.0, 6000));

        var m = FrameSeriesHygiene.Masque(frames);

        m.WarmupSecondsDropped.Should().Be(2, "la seconde 0 (l'image de 3 s) et la seconde 3 (images à 50 ms) sont du chargement");
        m.Garde.Skip(1).Take(20).Should().AllBeEquivalentTo(false);
        m.Garde.Skip(21).Should().AllBeEquivalentTo(true);
    }
}
