using System.Globalization;
using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 17/09/2026 — le bouton « tout annuler » est le filet de securite du produit :
// c'est lui qu'on cherche quand un reglage a fait empirer les choses. Il est
// Toujours visible, et desactive tant qu'aucun etat d'origine n'a ete capture.
//
// Un filet grise qui ne dit pas pourquoi est un cul-de-sac : la personne voit
// l'issue, la clique, rien ne se passe, et rien ne l'explique.
//
// Et le piege est double : meme en ajoutant une infobulle, WPF ne l'affiche
// Pas sur un controle desactive — il faut `ToolTipService.ShowOnDisabled="True"`.
// Une explication invisible exactement quand elle sert est pire que pas
// d'explication : on croit l'avoir donnee.
// -----------------------------------------------------------------------
public sealed class LeRetourArriereSExpliqueTests
{
    private static readonly ILocalizer Localise = new ResxLocalizer();

    private static string Xaml()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"));
    }

    [Fact]
    public void Sans_etat_capture_le_bouton_dit_pourquoi_il_ne_peut_rien_faire()
    {
        var vm = new DashboardViewModel();   // aucun instantane conserve

        vm.CanRevert.Should().BeFalse();
        vm.RevertHintKey.Should().Be("dashboard.revert.unavailable");
    }

    [Fact]
    public void Avec_un_etat_capture_il_annonce_ce_qu_il_va_faire()
    {
        // Le constructeur d'apercu (celui de `DashboardSample`) laisse la couture de
        // securite INERTE, c'est ecrit dans le code : un VM d'apercu ne peut donc jamais
        // annuler. Mon premier temoin l'ignorait et accusait le produit. Ici, la vraie
        // couture et un instantane conserve, comme apres un vrai `Apply`.
        var securite = NSubstitute.Substitute.For<DrDxt.Core.ISafetyManager>();
        securite.RevertAsync(NSubstitute.Arg.Any<DrDxt.Core.TweakSnapshot>())
                .Returns(DrDxt.Core.Result.Ok());
        var vm = new DashboardViewModel(
            securite, NSubstitute.Substitute.For<DrDxt.App.Navigation.INavigationService>(),
            () => new object(), cle => cle);
        vm.Load(new DrDxt.Core.ApplyReport(
            new[] { new DrDxt.Core.ApplyOutcome("fixit.power_plan", Applied: true, AutoReverted: false,
                                                new DrDxt.Core.TweakSnapshot("fixit.power_plan", "{}"), Error: null) },
            null));

        // Le temoin : si la propriete rendait toujours la meme cle, le test
        // precedent passerait sans rien garantir.
        vm.CanRevert.Should().BeTrue();
        vm.RevertHintKey.Should().Be("dashboard.revert.available");
    }

    [Theory]
    [InlineData("dashboard.revert.unavailable")]
    [InlineData("dashboard.revert.available")]
    public void Les_deux_explications_existent_en_francais_ET_en_anglais(string cle)
    {
        foreach (var culture in new[] { "fr", "en" })
        {
            var texte = Localise.Get(cle, new CultureInfo(culture));
            texte.Should().NotBeNullOrWhiteSpace().And.NotBe(cle, $"{cle} manque en {culture}");
        }
    }

    [Fact]
    public void L_infobulle_est_affichee_MEME_quand_le_bouton_est_desactive()
    {
        // Le piege WPF : sans cet attribut, l'explication reste invisible
        // precisement dans le seul cas ou elle sert.
        var xaml = Xaml();
        var balise = xaml[(xaml.IndexOf("Command=\"{Binding RevertCommand}\"", StringComparison.Ordinal) - 400)..];

        balise.Should().Contain("ToolTipService.ShowOnDisabled=\"True\"");
        balise[..(balise.IndexOf("</Button>", StringComparison.Ordinal) + 9)]
            .Should().Contain("RevertHintKey");
    }
}
