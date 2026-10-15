using System.Text.RegularExpressions;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Aucun pourcentage estime ne s'affiche comme un gain.
///
/// `Finding.EstimatedGainPct` n'est pas une mesure : la documentation de `ScoringRules` le dit,
/// « a finding's EstimatedGainPct is exactly the penalty the score removed ». C'est une
/// **estimation d'expert calibree a la main**. Rendue via le gabarit `Scan.Gain.UpTo` = « ~{0}% »,
/// elle ferait revenir un chiffre estime a cote de chaque constat.
///
/// Deux autres controles ne la voient pas : le balayage anti-promesse lit les chaines litterales
/// (ici le nombre est interpole a l'execution), et le test d'invariance compare deux scans sans
/// constat a gain estime.
///
/// La doctrine du produit tranche : un probleme prouve dont l'ampleur ne l'est pas se dit sans
/// nombre. Le constat nomme ce qui est mal regle ; il ne chiffre pas ce que ca coute.
/// </summary>
public sealed class NoEstimatedPercentIsShownAsAGainTests
{
    private static readonly Regex AnyPercent = new(@"\d+([.,]\d+)?\s*%", RegexOptions.Compiled);

    private static FindingViewModel View(double? gain) =>
        new(new Finding("Performance", Severity.Major, "Finding.Xmp", gain),
            key => key);

    [Theory]
    [InlineData(15.0)]
    [InlineData(2.5)]
    [InlineData(53.0)]
    public void A_finding_never_shows_an_estimated_percentage(double gain)
    {
        var vm = View(gain);

        AnyPercent.IsMatch(vm.GainLabel).Should().BeFalse(
            "cette valeur EST la penalite du score, une estimation calibree a la main : "
            + "l'afficher en pourcentage remet le chiffre que le produit a retire le 02/09");
    }

    [Fact]
    public void The_finding_still_names_what_is_wrong()
    {
        // L'autre bord : retirer le chiffre ne doit pas retirer le CONSTAT. Un client a qui on ne
        // dit plus rien est moins bien servi qu'un client a qui on donne une estimation.
        var vm = View(15.0);

        vm.Title.Should().Be("Finding.Xmp");
        vm.Severity.Should().Be(Severity.Major);
    }

    [Fact]
    public void The_percent_detector_would_actually_catch_one()
    {
        // Controle POSITIF : sans lui, une expression reguliere cassee rendrait la meme couleur
        // qu'un ecran sain — deja arrive deux fois dans cette session.
        AnyPercent.IsMatch("~15%").Should().BeTrue();
        AnyPercent.IsMatch("jusqu'a 2,5 %").Should().BeTrue();
        AnyPercent.IsMatch("XMP desactive").Should().BeFalse();
    }
}
