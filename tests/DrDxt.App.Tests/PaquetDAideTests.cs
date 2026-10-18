using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le paquet d'aide se lit avant d'être écrit : chaque fichier de la liste montre le texte exact qui entrera dans le zip,
/// déjà anonymisé, et la taille porte l'unité de la langue de l'App.
/// </summary>
public sealed class PaquetDAideTests
{
    [Fact]
    public void Choisir_un_fichier_montre_son_texte_exact_avant_l_ecriture()
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(),
            k => k == "Unite.Ko" ? "KB" : k);

        vm.PoserPaquet([("rapport-du-docteur.txt", "rapport"), ("crash.log", "pile <profil>")]);

        vm.LignesPaquet.Should().Equal("rapport-du-docteur.txt (1 KB)", "crash.log (1 KB)");
        vm.ApercuPaquet.Should().BeEmpty("rien n'est choisi");
        vm.PaquetChoisi = 1;
        vm.ApercuPaquet.Should().Be("pile <profil>");
        vm.PaquetChoisi = -1;
        vm.ApercuPaquet.Should().BeEmpty();
    }
}
