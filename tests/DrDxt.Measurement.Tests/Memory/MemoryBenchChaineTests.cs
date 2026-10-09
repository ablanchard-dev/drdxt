using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

// La latence se mesure en suivant une chaine de pointeurs. Elle ne mesure la zone annoncee que si,
// partant de 0, on passe par CHAQUE element avant de revenir : sinon la fenetre de 2 Mo n'en parcourt
// qu'une partie. Les tailles sont celles des fenetres du banc, plus de petites valeurs limites.
public sealed class MemoryBenchChaineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8192)]      // 32 Ko d'entiers
    [InlineData(65536)]     // 256 Ko
    [InlineData(524288)]    // 2 Mo
    public void La_chaine_passe_par_chaque_element_avant_de_revenir_a_0(int elements)
    {
        var chaine = MemoryBench.ChaineUnique(elements);

        var vus = new bool[elements];
        int q = 0, pas = 0;
        do
        {
            vus[q].Should().BeFalse("un element revu avant la fin est un cycle trop court");
            vus[q] = true;
            q = chaine[q];
            pas++;
        } while (q != 0 && pas <= elements);

        pas.Should().Be(elements);
    }

    [Fact]
    public void La_chaine_est_la_meme_d_une_execution_a_l_autre()
        => MemoryBench.ChaineUnique(4096).Should().Equal(MemoryBench.ChaineUnique(4096));
}
