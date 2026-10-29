using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Les commandes asynchrones ne passent pas FlowExceptionsToTaskScheduler : une exception remonte au fil d'interface, ouvre la
// boîte de plantage et laisse l'écran sans résultat. Chacune la rattrape et dit la raison là où son résultat s'affiche.
public sealed class CommandesQuiRattrapentTests
{
    [Fact]
    public async Task Une_mesure_de_cadence_qui_leve_dit_sa_raison()
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
        vm.MesureDesEntrees = _ => throw new InvalidOperationException("souris débranchée");

        await vm.Invoking(v => v.MesurerCadenceCommand.ExecuteAsync(null)).Should().NotThrowAsync();

        vm.ResultatCadence.Should().NotBeNullOrEmpty();
        vm.CadenceEnCours.Should().BeFalse();
    }

    // Un retrait automatique rend des réglages par le helper : « Annuler » sur le même levier rendrait deux fois le même
    // instantané. Il attend la fin du retrait, comme il attend Optimiser.
    [Fact]
    public void Annuler_attend_un_retrait_automatique()
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
        vm.AucuneEcritureEnCours.Should().BeTrue("témoin : rien ne tourne");

        vm.RetraitAutomatiqueEnCours = true;

        vm.AucuneEcritureEnCours.Should().BeFalse();
        vm.AnnulerLevierCommand.CanExecute("latency.hagsOn").Should().BeFalse();
    }

    // Fermer pendant une vérification ou Optimiser laisse l'écriture à moitié : la fenêtre le demande avant de fermer.
    [Fact]
    public void Fermer_pendant_une_ecriture_longue_se_demande()
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
        vm.AvertissementDeFermeture.Should().BeNull("témoin : rien ne tourne, rien n'est demandé");

        vm.VerificationEnCours = true;

        vm.AvertissementDeFermeture.Should().Be("Fermeture.EcritureEnCours");
    }

    // Les sept commandes relevées sans rattrapage : chacune a désormais son catch.
    [Theory]
    [InlineData("ScanViewModel.Appliquer.cs", "ApplyAsync")]
    [InlineData("ScanViewModel.TestMemoire.cs", "MesurerCadence")]
    [InlineData("ScanViewModel.TestMemoire.cs", "LancerTestMemoire")]
    [InlineData("ScanViewModel.Agir.cs", "BasculerPlafondEnvoi")]
    [InlineData("ScanViewModel.Expert.cs", "PoserEssaiAsync")]
    [InlineData("ScanViewModel.Expert.cs", "RendreCarteExpertAsync")]
    [InlineData("ScanViewModel.PaquetDAide.cs", "PreparerPaquetAsync")]
    public void La_commande_rattrape_ses_exceptions(string fichier, string methode)
        => CorpsDe(Path.Combine("ViewModels", fichier), $@"private async Task {methode}\(", methode).Should().Contain("catch (Exception", methode);

    // Les tâches lancées sans attente (« _ = … ») : personne n'observe leur exception, qui laissait l'écran en attente.
    [Theory]
    [InlineData("ViewModels/ScanViewModel.ReposCarte.cs", @"internal async Task ConclureReposCarteAsync\(")]
    [InlineData("ViewModels/ScanViewModel.ReposCarte.cs", @"internal async Task SurveillerLaCarteEnJeuAsync\(")]
    [InlineData("ViewModels/ScanViewModel.Scan.cs", @"public async Task LoadIdleComponentsAsync\(")]
    [InlineData("App.xaml.cs", @"private static async Task VerifierMisesAJourAsync\(")]
    public void La_tache_sans_attente_rattrape_ses_exceptions(string fichier, string signature)
        => CorpsDe(fichier, signature, signature).Should().Contain("catch (Exception", signature);

    private static string CorpsDe(string fichier, string signature, string nom)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var texte = File.ReadAllText(Path.Combine(dir!.FullName, "src", "DrDxt.App", fichier));
        var debut = Regex.Match(texte, signature);
        var methode = nom;
        debut.Success.Should().BeTrue($"témoin : {methode} est trouvée");
        var suite = texte[debut.Index..];
        var fin = Regex.Match(suite[1..], @"\n    (private|public|internal|\[|/// <summary>)");
        return fin.Success ? suite[..(fin.Index + 1)] : suite;
    }
}
