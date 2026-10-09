using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>09/10/2026 (audit des gains) : « fréquence − 3 » est un minimum ; aux hautes fréquences la cible suit Reflex.</summary>
public sealed class PlafondVrrTests
{
    [Theory]
    [InlineData(60, 57)]     // fréquence − 3 est la plus basse
    [InlineData(144, 138)]   // Reflex mesuré par Blur Busters : ~138
    [InlineData(165, 157)]
    [InlineData(200, 188)]   // l'écran principal de la machine de référence : 197 avant, trop près de la Vsync
    [InlineData(240, 224)]   // Reflex mesuré : ~224
    public void La_cible_est_la_plus_basse_de_frequence_moins_3_et_de_reflex(int hz, int attendu)
        => PlafondVrr.Cible(hz).Should().Be(attendu);

    [Theory]
    [InlineData(141, 144, true)]    // posé par une ancienne version (fréquence − 3) : tenu pour bon
    [InlineData(138, 144, true)]
    [InlineData(137, 144, false)]   // plus bas que nécessaire : coûte des images
    [InlineData(142, 144, false)]   // trop près de la Vsync
    [InlineData(197, 200, true)]
    [InlineData(141, 200, false)]   // la valeur du second écran 144 Hz, pas du principal
    public void Un_plafond_entre_la_cible_et_frequence_moins_3_est_bas_latence(int plafond, int hz, bool attendu)
        => PlafondVrr.EstBasseLatence(plafond, hz).Should().Be(attendu);

    [Fact]
    public void Une_frequence_absurde_ne_donne_jamais_zero_ni_negatif()
    {
        PlafondVrr.Cible(0).Should().Be(1);
        PlafondVrr.Cible(3).Should().Be(1);
        PlafondVrr.EstBasseLatence(1, 0).Should().BeFalse();
        PlafondVrr.Cible(3600).Should().Be(1800, "la formule tomberait à 0 : la moitié de la fréquence est le plancher");
        PlafondVrr.Cible(2000).Should().Be(1000);
    }
}
