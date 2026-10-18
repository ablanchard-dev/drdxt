using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le test de la ligne sous charge part du seul clic : sans couture posée par l'App, pas de bouton ; avec, rien ne part
/// avant la commande, puis le compte rendu reste sous le bouton.
/// </summary>
public class TestDeLigneSurClicTests
{
    // Le gabarit factice porte autant de trous que le vrai texte : deux pour la description, un pour l'échec.
    private static ViewModels.ScanViewModel Vm(string trous = " {0} {1}")
        => ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k + trous);

    [Fact]
    public async Task Rien_ne_part_avant_le_clic_puis_le_compte_rendu_s_affiche()
    {
        var vm = Vm();
        vm.TestLigneDisponible.Should().BeFalse("sans couture, pas de bouton");

        var appels = 0;
        vm.TesterLaLigne = _ => { appels++; return Task.FromResult("compte rendu"); };
        vm.TestLigneDisponible.Should().BeTrue();
        appels.Should().Be(0, "poser la couture ne lance rien");
        vm.LabelTestLigneDescription.Should().Contain("speed.cloudflare.com").And.Contain("400", "le volume est dit avant le clic");

        await vm.TesterLigneCommand.ExecuteAsync(null);

        appels.Should().Be(1);
        vm.TexteTestLigne.Should().Be("compte rendu");
        vm.TestLigneEnCours.Should().BeFalse();
    }

    [Fact]
    public async Task Arreter_annule_le_test_en_cours_et_le_dit()
    {
        var vm = Vm();
        var recu = CancellationToken.None;
        var demarre = new TaskCompletionSource();
        vm.TesterLaLigne = async ct =>
        {
            recu = ct;
            demarre.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "jamais";
        };

        var test = vm.TesterLigneCommand.ExecuteAsync(null);
        await demarre.Task;
        vm.TestLigneEnCours.Should().BeTrue();
        vm.ArreterTestLigneCommand.Execute(null);
        await test;

        recu.IsCancellationRequested.Should().BeTrue("le trafic s'arrête quand le joueur arrête");
        vm.TexteTestLigne.Should().StartWith("Ligne.Test.Annule");
        vm.TestLigneEnCours.Should().BeFalse();
    }

    [Fact]
    public async Task Un_echec_se_dit_au_lieu_de_se_taire()
    {
        var vm = Vm(" {0}");
        vm.TesterLaLigne = _ => throw new System.Net.Http.HttpRequestException("hors ligne");
        await vm.TesterLigneCommand.ExecuteAsync(null);
        vm.TexteTestLigne.Should().Contain("Ligne.Test.Echec").And.Contain("hors ligne");
    }
}
