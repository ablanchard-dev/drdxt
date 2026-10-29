using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;
using Xunit;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

/// <summary>
/// Quand un souci est en cours, le point que l'Optimiser va prendre le contiendra. L'écran le dit
/// AVANT le clic, avec le point sain que le docteur a trouvé (ou l'absence d'un tel point).
/// </summary>
public class PointPendantUnSouciTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "560.0", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static ViewModels.ScanViewModel Vm(params RepairFinding[] constats)
    {
        var st = new SettingsState(TriState.On, TriState.On,
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On,
            Array.Empty<int>(), TriState.Unknown);
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), st, new List<Finding>(), bl)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k.EndsWith(".DuringIssue") ? k + " {0} | {1}" : k + " {0}",
            repairAudit: () => constats);
    }

    private static RepairFinding Sain(string? valeur, RepairStatus statut = RepairStatus.Observed,
        RepairAction action = RepairAction.Recommandation)
        => new("STABILITE", "point de restauration sain", valeur, null, statut, "explication", action, Souci);

    private const string Souci = "arrêt brutal pendant une mise en veille, le 05/10";

    // L'avertissement commence par le souci : sa première lettre prend la majuscule.
    private const string SouciAffiche = "Arrêt brutal pendant une mise en veille, le 05/10";

    [Fact]
    public async Task Un_souci_en_cours_est_dit_avant_le_clic_avec_le_point_sain()
    {
        var vm = Vm(Sain("« Windows Update » du 18/09/2026 à 12:00"));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.AvertissementPoint.Should().Contain("Apply.RestorePoint.DuringIssue").And.Contain("« Windows Update » du 18/09/2026")
            .And.Contain(SouciAffiche, "capture du 06/10 : « un souci est en cours » sans dire lequel");
    }

    // Sans point d'avant le souci, « Pour revenir à un état sain : aucun point d'avant le souci »
    // ne voulait rien dire ; une seconde phrase le dit.
    [Fact]
    public async Task Sans_point_d_avant_le_souci_l_avertissement_le_dit_sans_proposer_de_retour()
    {
        var vm = Vm(Sain("aucun point d'avant le souci", action: RepairAction.Aucune));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.AvertissementPoint.Should().StartWith("Apply.RestorePoint.DuringIssueNoPoint").And.NotContain("aucun point d'avant").And.Contain(SouciAffiche);
    }

    // Le bilan santé se remplit des constats du scan, jusqu'au ViewModel.
    [Fact]
    public async Task Le_bilan_sante_prend_les_constats_du_scan()
    {
        var vm = Vm(Sain("« Windows Update » du 18/09/2026 à 12:00"));
        vm.Bilan.Should().HaveCount(8).And.OnlyContain(l => l.Constat.Etat != EtatConstat.Lu);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Bilan[7].Constat.Valeur.Should().Be("« Windows Update » du 18/09/2026 à 12:00");
        vm.Bilan[0].Constat.Etat.Should().Be(EtatConstat.NonLu);
    }

    [Fact]
    public async Task Sans_souci_en_cours_rien_n_est_dit()
    {
        var vm = Vm();
        await vm.ScanCommand.ExecuteAsync(null);

        vm.AvertissementPoint.Should().BeNull();
    }

    // Le docteur ne rend ce constat « non lu » qu'avec un souci en cours ; le point de l'Optimiser le
    // contiendra quand même : c'est dit, sans nommer de point.
    [Fact]
    public async Task Une_liste_de_points_illisible_pendant_un_souci_avertit_sans_nommer_de_point()
    {
        var vm = Vm(Sain(null, RepairStatus.Unknown));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.AvertissementPoint.Should().Be("Apply.RestorePoint.DuringIssueUnread " + SouciAffiche);
    }

    // L'avertissement s'écrivait en gris SOUS le bouton Optimiser, lu après le clic.
    // Il vient avant le bouton, en couleur « à regarder ».
    [Fact]
    public void L_avertissement_du_souci_precede_le_bouton_Optimiser()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = VueScan.Texte(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var avertissement = xaml.IndexOf("{Binding AvertissementPoint}", StringComparison.Ordinal);

        avertissement.Should().BePositive("témoin : l'avertissement est affiché");
        avertissement.Should().BeLessThan(xaml.IndexOf("x:Name=\"BoutonOptimiser\"", StringComparison.Ordinal));
        // Ses mots le disent, en texte plein : la couleur d'état ne vit que dans la navigation et les titres de groupe.
        xaml.Substring(avertissement, 300).Should().Contain("TexteBrush").And.NotContain("EtatRegarderBrush");
    }
}
