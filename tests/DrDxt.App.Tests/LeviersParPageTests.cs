using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App.Tests;

/// <summary>
/// ROAD1 (08/10/2026) : « ce que DrDXT fait sur ton PC », page par page. Chaque levier du catalogue apparaît sur sa page,
/// avec son nom, son statut et, quand il n'est pas pour ce matériel, sa raison.
/// </summary>
public sealed class LeviersParPageTests
{
    [Fact]
    public void Chaque_levier_se_range_sur_sa_page_avec_nom_statut_et_raison()
    {
        var pages = CarteDeLaMachine.LeviersParPage(
        [
            new LevierSurCeMateriel("gpu.power_limit", StatutDuLevier.PasIci, "Levier.PasIci.FrequencesVerrouillees"),
            new LevierSurCeMateriel("latency.gameModeOn", StatutDuLevier.Pose),
        ], k => k);

        var carte = pages["graphique"].Single();
        carte.Nom.Should().Be("tweak.gpu.power_limit.title");
        carte.Statut.Should().Be("Levier.Statut.PasIci");
        carte.Raison.Should().Be("Levier.PasIci.FrequencesVerrouillees");
        pages["latence"].Single().Raison.Should().BeNull();
    }

    [Fact]
    public async Task Apres_un_scan_la_page_Graphique_liste_ses_leviers()
    {
        var hw = new DrDxt.Core.HardwareInventory(new DrDxt.Core.CpuInfo("CPU", "AMD", 8, 16, false, 1),
            new[] { new DrDxt.Core.GpuInfo("GPU", "AMD", 16L << 30, "1", DrDxt.Core.TriState.On) },
            new[] { new DrDxt.Core.MemoryModule("PN", "C", 6000, 6000) }, new DrDxt.Core.MotherboardInfo("M", "B"),
            new DrDxt.Core.BiosInfo("1", DateTimeOffset.UtcNow, true), DrDxt.Core.ChassisType.Desktop);
        var st = new DrDxt.Core.SettingsState(DrDxt.Core.TriState.On, DrDxt.Core.TriState.On,
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", DrDxt.Core.TriState.On, DrDxt.Core.TriState.On, Array.Empty<int>(), DrDxt.Core.TriState.Unknown);
        var hd = NSubstitute.Substitute.For<DrDxt.Core.IHardwareDetector>();
        NSubstitute.SubstituteExtensions.Returns(hd.DetectAsync(), DrDxt.Core.Result<DrDxt.Core.HardwareInventory>.Ok(hw));
        var sr = NSubstitute.Substitute.For<DrDxt.Core.ISettingsStateReader>();
        NSubstitute.SubstituteExtensions.Returns(sr.ReadAsync(NSubstitute.Arg.Any<DrDxt.Core.HardwareInventory>()), DrDxt.Core.Result<DrDxt.Core.SettingsState>.Ok(st));
        var bl = new DrDxt.Core.BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var br = NSubstitute.Substitute.For<DrDxt.Core.IBenchmarkRunner>();
        NSubstitute.SubstituteExtensions.Returns(br.RunAsync(NSubstitute.Arg.Any<int>(), NSubstitute.Arg.Any<TimeSpan>(), NSubstitute.Arg.Any<int>(),
            NSubstitute.Arg.Any<CancellationToken>()), DrDxt.Core.Result<DrDxt.Core.BenchmarkResult>.Ok(bl));
        var sc = NSubstitute.Substitute.For<DrDxt.Core.IScanScorer>();
        NSubstitute.SubstituteExtensions.Returns(sc.Score(NSubstitute.Arg.Any<DrDxt.Core.HardwareInventory>(), NSubstitute.Arg.Any<DrDxt.Core.SettingsState>(),
            NSubstitute.Arg.Any<DrDxt.Core.BenchmarkResult?>()), DrDxt.Core.Result<DrDxt.Core.ScanResult>.Ok(new DrDxt.Core.ScanResult(hw, st,
            new List<DrDxt.Core.Finding>(), 68, bl, new DrDxt.Core.ScoreBreakdown(80, 80, 85), new DrDxt.Core.DimensionStatus(false, false))));
        var sd = NSubstitute.Substitute.For<DrDxt.Core.IStorageDetector>();
        NSubstitute.SubstituteExtensions.Returns(sd.Detect(), DrDxt.Core.Result<IReadOnlyList<DrDxt.Core.StorageDevice>>.Ok(new List<DrDxt.Core.StorageDevice>()));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k);
        var levier = NSubstitute.Substitute.For<DrDxt.Core.ITweak>();
        NSubstitute.SubstituteExtensions.Returns(levier.Id, "gpu.power_limit");
        NSubstitute.SubstituteExtensions.Returns(levier.AppliesTo(NSubstitute.Arg.Any<DrDxt.Core.HardwareInventory>(), NSubstitute.Arg.Any<DrDxt.Core.SettingsState>()), true);
        vm.CatalogueDesLeviers = () => [levier];

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Domaines.Single(d => d.Cle == "graphique").Leviers.Should().ContainSingle()
            .Which.Should().Be(new LigneLevier("tweak.gpu.power_limit.title", "Levier.Statut.APoser", null, StatutDuLevier.APoser));
    }

    [Fact]
    public void Chaque_levier_du_vrai_catalogue_a_une_page_choisie_et_un_nom()
    {
        using var sp = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
        var catalogue = sp.GetRequiredService<DrDxt.Core.ITweakCatalog>().All();

        catalogue.Should().HaveCountGreaterThan(10, "témoin : le vrai catalogue est lu");
        catalogue.Where(t => !CouvertureDesLeviers.DomaineChoisi(t.Id)).Select(t => t.Id)
            .Should().BeEmpty("un levier sans page choisie tomberait sur Windows sans que personne l'ait décidé");
        catalogue.Where(t => TweakDisplayKeys.TitleKey(t.Id) == t.Id).Select(t => t.Id)
            .Should().BeEmpty("un levier sans nom afficherait son identifiant");
    }
}
