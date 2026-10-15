using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

public class FindingViewModelTests
{
    [Fact]
    public void Maps_finding_to_localized_title_and_shows_NO_estimated_gain()
    {
        // Un constat n'affiche pas la penalite estimee en pourcentage : `EstimatedGainPct` est la
        // penalite (documentation de ScoringRules), une estimation calibree a la main. Le constat
        // nomme ce qui est mal regle ; il ne chiffre pas ce que ca coute.
        var f = new Finding("Memory", Severity.Major, "Finding.Memory.XmpOff", 12.0);
        var vm = new FindingViewModel(f, key => key == "Finding.Memory.XmpOff" ? "Mémoire : XMP off" : key);

        vm.Title.Should().Be("Mémoire : XMP off", "le constat, lui, reste dit");
        vm.GainLabel.Should().BeEmpty("une estimation ne s'affiche pas comme un gain");
    }

    [Fact]
    public void Info_finding_without_gain_has_no_gain_label()
    {
        var f = new Finding("Gpu", Severity.Info, "Finding.Gpu.ReBarUnknown", null);
        var vm = new FindingViewModel(f, k => k);
        vm.GainLabel.Should().BeEmpty();
    }

    [Fact]
    public void Every_finding_names_what_it_found()
    {
        // DrDXT est gratuit. Avant, un constat au correctif « payant » ne montrait
        // qu'une accroche (« Mémoire (RAM) — problème trouvé ») et cachait le reglage jusqu'a
        // l'achat. Il n'y a plus rien a acheter : le constat dit toujours ce qu'il a trouve.
        var f = new Finding("Memory", Severity.Major, "Finding.Memory.XmpOff", 12.0);
        var vm = new FindingViewModel(f, k => k == "Finding.Memory.XmpOff" ? "Mémoire : XMP off" : k);

        vm.Title.Should().Contain("XMP");
        typeof(FindingViewModel).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "IsLocked", "TeaseHeadline", "CtaLabel" },
                "une vue verrouillee n'a plus de raison d'exister");
    }
}
