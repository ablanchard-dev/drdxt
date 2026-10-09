using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Le scan traite proprement un detecteur qui refuse : `Result.Fail`
// devient `Scan.Error.Detection`, l'ecran le dit, la vie continue. Mais rien ne
// couvrait le detecteur qui LEVE.
//
// Or les sondes du scan parlent a WMI, au registre, a des pilotes et a des
// services : c'est precisement le genre de code qui leve au lieu de refuser --
// un service arrete, une cle absente, un pilote qui rend n'importe quoi. Sans
// garde, l'exception remonte jusqu'au filet fatal de App.xaml.cs : le rapport de
// panne est ecrit, la boite de dialogue s'affiche, et **l'application se ferme
// au milieu du scan**.
//
// Sur un produit d'optimisation, c'est le pire moment pour disparaitre : la
// personne vient de lancer la seule chose qu'elle voulait faire.
// -----------------------------------------------------------------------
public sealed class UnScanQuiCasseNeTuePasLAppTests
{
    private static IStorageDetector Stockage()
    {
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));
        return sd;
    }

    private static ScanViewModel AvecUnDetecteurQuiLeve(Exception panne)
    {
        var detecteur = Substitute.For<IHardwareDetector>();
        detecteur.DetectAsync().Returns<Task<Result<HardwareInventory>>>(_ => throw panne);

        return ScanViewModelTestSeams.Build(
            detecteur,
            Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(),
            Substitute.For<IScanScorer>(),
            Stockage(),
            cle => cle);
    }

    [Fact]
    public async Task Une_sonde_qui_leve_ne_fait_pas_remonter_l_exception()
    {
        var vm = AvecUnDetecteurQuiLeve(new InvalidOperationException("WMI ne repond pas"));

        var geste = async () => await vm.ScanCommand.ExecuteAsync(null);

        await geste.Should().NotThrowAsync(
            "une exception qui sort d'ici atteint le filet fatal de App.xaml.cs, " +
            "et l'application se ferme au milieu du scan");
    }

    [Fact]
    public async Task L_ecran_reste_utilisable_et_dit_ce_qui_s_est_passe()
    {
        var vm = AvecUnDetecteurQuiLeve(new InvalidOperationException("WMI ne repond pas"));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.State.Should().NotBe(ScanState.Scanning, "l'ecran ne doit pas rester bloque a tourner");
        vm.ErrorMessage.Should().NotBeNullOrWhiteSpace("la personne doit lire ce qui n'a pas marche");
    }

    [Fact]
    public async Task Le_detail_technique_ne_part_pas_a_l_ecran()
    {
        // Le message d'une exception n'est pas ecrit pour un client : il peut contenir un
        // chemin, un nom de machine, une requete. L'ecran dit ce qui s'est passe, le
        // detail vit dans le journal.
        var vm = AvecUnDetecteurQuiLeve(new InvalidOperationException("WMI ne repond pas"));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().NotContain("WMI ne repond pas");
    }

    [Fact]
    public async Task Le_temoin_un_detecteur_qui_refuse_proprement_est_toujours_traite()
    {
        // Sans ce temoin, on pourrait « reparer » le cas qui leve en cassant celui qui
        // refuse -- et c'est celui-la qui arrive tous les jours.
        var detecteur = Substitute.For<IHardwareDetector>();
        detecteur.DetectAsync().Returns(Result<HardwareInventory>.Fail("HW_FAIL", "refus propre"));

        var vm = ScanViewModelTestSeams.Build(
            detecteur,
            Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(),
            Substitute.For<IScanScorer>(),
            Stockage(),
            cle => cle);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Be("Scan.Error.Detection");
    }
}
