using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Un chiffre juste, plausible, et vide de sens.
///
/// Le 08/09/2026, la premiere lecture live des grandeurs de cadence a rendu
/// `animationError_p95 = 1186,95 ms` sur huit frames captees d'un terminal en dix secondes. Le
/// calcul etait correct, l'unite juste, aucune exception levee — et le nombre ne disait rien :
/// a 0,8 image par seconde il recopiait l'intervalle entre deux images.
///
/// Ces tests gravent la seule chose qui protege de ce defaut : un p95 ne merite son nom qu'a
/// partir de vingt echantillons, faute de quoi il se confond avec le maximum.
/// </summary>
public class TailStatisticTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]   // la capture reelle qui a revele le defaut
    [InlineData(19)]
    public void SOUS_VINGT_ECHANTILLONS_UN_P95_SE_CONFOND_AVEC_LE_MAXIMUM(int n)
    {
        // Ce n'est pas un seuil de prudence, c'est de l'arithmetique : 1/20 = 5 %. En dessous,
        // le 95e centile ne peut ecarter aucun point du maximum.
        TailStatistic.P95EstInterpretable(n).Should().BeFalse();
        TailStatistic.PourquoiPasInterpretable(n).Should().NotBeNull();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(600)]
    public void A_PARTIR_DE_VINGT_IL_DECRIT_VRAIMENT_UNE_QUEUE(int n)
    {
        // Le controle POSITIF : sans lui, la garde pourrait tout refuser et rendre la grandeur
        // inutilisable partout, ce qui serait la faute inverse et tout aussi couteuse.
        TailStatistic.P95EstInterpretable(n).Should().BeTrue();
        TailStatistic.PourquoiPasInterpretable(n).Should().BeNull();
    }

    [Fact]
    public void LA_RAISON_EST_UNE_PHRASE_QUI_DIT_LE_NOMBRE_DE_FRAMES()
    {
        // « p95 non interpretable » sans dire pourquoi se lit comme une panne de l'outil, alors
        // que c'est une propriete de la CAPTURE. La phrase doit porter le compte reel pour que le
        // lecteur sache quoi changer : capturer plus longtemps.
        TailStatistic.PourquoiPasInterpretable(8)
            .Should().Contain("8").And.Contain("20");
    }
}
