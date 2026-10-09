using System.Globalization;
using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 17/09/2026, suite du « tout annuler » : le defaut n'etait pas un cas isole.
// Six commandes de l'application peuvent etre grisees (activer, transferer,
// rafraichir, acheter, installer la mise a jour, APPLIQUER) et **aucune**
// n'expliquait son inertie.
//
// La plus lourde de consequences est « Appliquer » : c'est l'action principale
// du produit. Grisee avant tout scan, elle laisse la personne devant un bouton
// mort sans savoir s'il est casse ou s'il attend quelque chose d'elle.
//
// Meme piege que la veille : sans `ToolTipService.ShowOnDisabled`, WPF cache
// l'explication exactement quand elle sert.
// -----------------------------------------------------------------------
public sealed class LActionPrincipaleSExpliqueTests
{
    private static readonly ILocalizer Localise = new ResxLocalizer();

    private static ScanViewModel Neuf() => new(
        Substitute.For<IHardwareDetector>(),
        Substitute.For<ISettingsStateReader>(),
        Substitute.For<IBenchmarkRunner>(),
        Substitute.For<IScanScorer>(),
        Substitute.For<IStorageDetector>(),
        ScanViewModelTestSeams.Safety(),
        ScanViewModelTestSeams.Engine(),
        ScanViewModelTestSeams.ProfileConsumer(),
        ScanViewModelTestSeams.GpuTuningStep(),
        Substitute.For<DrDxt.App.Navigation.INavigationService>(),
        () => new DashboardViewModel(),
        ScanViewModelTestSeams.DesiredStateStore(),
        new AppliedTweakManifestMapper(),
        ScanViewModelTestSeams.BiosAdvisor(),
        ScanViewModelTestSeams.GuideGenerator(),
        cle => cle);

    private static string Xaml()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
    }

    [Fact]
    public void Avant_tout_scan_le_bouton_appliquer_dit_ce_qu_il_attend()
    {
        var vm = Neuf();

        vm.CanApply.Should().BeFalse();
        vm.ApplyHintKey.Should().Be("scan.apply.needs_scan");
    }

    [Fact]
    public void Une_fois_le_scan_fait_il_annonce_ce_qu_il_va_faire()
    {
        // Le temoin : une propriete qui rendrait toujours la meme cle ferait
        // passer le test precedent sans rien garantir.
        var vm = Neuf();
        vm.State = ScanState.Done;

        vm.CanApply.Should().BeTrue();
        vm.ApplyHintKey.Should().Be("scan.apply.ready");
    }

    [Theory]
    [InlineData("scan.apply.needs_scan")]
    [InlineData("scan.apply.ready")]
    public void Les_deux_explications_existent_en_francais_ET_en_anglais(string cle)
    {
        foreach (var culture in new[] { "fr", "en" })
        {
            var texte = Localise.Get(cle, new CultureInfo(culture));
            texte.Should().NotBeNullOrWhiteSpace().And.NotBe(cle, $"{cle} manque en {culture}");
        }
    }

    [Fact]
    public void L_explication_est_affichee_MEME_quand_le_bouton_est_desactive()
    {
        var xaml = Xaml();
        var depart = xaml.IndexOf("Command=\"{Binding ApplyCommand}\"", StringComparison.Ordinal);
        depart.Should().BeGreaterThan(0, "le bouton Appliquer doit exister dans ScanView");
        var bloc = xaml[Math.Max(0, depart - 500)..Math.Min(xaml.Length, depart + 500)];

        bloc.Should().Contain("ToolTipService.ShowOnDisabled=\"True\"");
        bloc.Should().Contain("ApplyHint");

        // Le convertisseur "Loc" n'existe que dans DashboardView : lier une cle ici
        // leverait au rendu. Ce test l'interdit explicitement, parce qu'un grep de XAML
        // ne voit pas une ressource manquante — seul le crible de liaisons la voit.
        bloc.Should().NotContain("StaticResource Loc");
    }
}
