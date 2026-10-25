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

    // La zone « DRAM » déborde du L3 lu : sur un X3D (96 Mo), 128 Mio fixes laissaient une part des accès dans le cache.
    [Theory]
    [InlineData(0L, 128L * 1024 * 1024)]                         // L3 non lu : le plancher
    [InlineData(16L * 1024 * 1024, 128L * 1024 * 1024)]          // 16 Mo de L3 : le plancher suffit
    [InlineData(96L * 1024 * 1024, 4 * 96L * 1024 * 1024)]       // 5800X3D, 96 Mo
    [InlineData(128L * 1024 * 1024, 4 * 128L * 1024 * 1024)]     // 9950X3D, 2 x 64 Mo
    public void La_zone_de_latence_vaut_au_moins_quatre_fois_le_l3(long l3, long octetsAttendus)
        => ((long)MemoryBench.ElementsLatence(l3) * sizeof(int)).Should().Be(octetsAttendus);

    [Fact]
    public void Le_l3_se_lit_sans_lever()
        => MemoryBench.OctetsL3().Should().BeGreaterThanOrEqualTo(0);

    // Le débit multi-fil est le seul qui puisse dire « un seul canal ». Une analyse antivirus pendant une série le faisait
    // tomber sous le plafond d'un canal sur une machine en double canal. Trois séries trop dispersées ne concluent rien.
    [Theory]
    [InlineData(50.0, 52.0, 51.0, 51.0)]
    [InlineData(24.0, 50.0, 51.0, 0.0)]
    [InlineData(24.0, 25.0, 40.0, 0.0)]
    [InlineData(25.0, 25.5, 26.0, 25.5)]
    public void Trois_series_dispersees_ne_concluent_rien(double a, double b, double c, double attendu)
        => MemoryBench.MedianeSiStable(a, b, c).Should().Be(attendu);
}
