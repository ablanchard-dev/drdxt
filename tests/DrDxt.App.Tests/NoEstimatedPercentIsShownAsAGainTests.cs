using System.Text.RegularExpressions;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Le dernier reste du score, et il etait en facade.
///
/// Mesure du 05/09/2026. `Finding.EstimatedGainPct` n'est pas une mesure : la documentation de
/// `ScoringRules` le dit mot pour mot — « a finding's EstimatedGainPct is exactly the penalty the
/// score removed ». C'est donc une **estimation d'expert calibree a la main**, la meme grandeur
/// que celle pour laquelle le score /100 a ete retire de la vitrine le 02/09.
///
/// Et `FindingViewModel` la rendait au client via le gabarit `Scan.Gain.UpTo` = « ~{0}% ».
/// Autrement dit : le chiffre chasse de l'entete revenait, ligne par ligne, a cote de chaque
/// constat.
///
/// Deux controles ne l'ont pas vu, et les deux pour une raison precise :
///   • le balayage anti-promesse (60fdbd0) lit les chaines LITTERALES ; ici le nombre est
///     interpole a l'execution, donc invisible dans le resx ;
///   • le test d'invariance (a288ec3) compare deux scans dont le decor ne porte aucun constat
///     avec gain estime — l'echantillon ne contenait pas le cas.
/// C'est la troisieme fois de la soiree qu'une garde juste rate ce qu'elle ne regarde pas.
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
