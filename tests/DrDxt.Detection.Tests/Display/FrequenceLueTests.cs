using DrDxt.Detection.Display;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

// Win32 rend 0 ou 1 dans dmDisplayFrequency pour « la fréquence par défaut du matériel » (DEVMODE, Microsoft Learn) : ce
// n'est pas une fréquence. Rendue telle quelle, 1 Hz faisait voir une marge de 143 Hz à l'heuristique (le levier de
// fréquence proposait d'écrire un mode d'écran) et un plafond VRR de 1 image/s. Lue, elle sort 0 : non lue.
public sealed class FrequenceLueTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(60, 60)]
    [InlineData(200, 200)]
    public void Le_defaut_du_materiel_n_est_pas_une_frequence(int dm, int lue)
        => Win32DisplayInfo.FrequenceLue(dm).Should().Be(lue);
}
