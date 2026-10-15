using DrDxt.App.Effets;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// « la jauge pas même synchro que le scanner, quand le scan est fini la jauge pas
// pleine ». Chaque étape pèse son temps réel ; la jauge avance au rythme du temps ; à la fin elle est pleine et reste
// visible assez longtemps pour finir de glisser.
public sealed class LaJaugeSuitLeTempsDeLAnalyseTests
{
    [Fact]
    public void Chaque_etape_occupe_une_part_de_jauge_proportionnelle_a_sa_duree()
    {
        double[] durees = [1, 1, 1, 5, 1, 1, 0.5, 1.5];
        var (debut, fin) = ScanViewModel.Bornes(durees, 4);
        debut.Should().BeApproximately(3 / 12.0, 1e-9);
        (fin - debut).Should().BeApproximately(5 / 12.0, 1e-9, "l'étape longue prend la plus grande part");
        ScanViewModel.Bornes(durees, 8).Fin.Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void Pendant_une_etape_la_jauge_avance_au_rythme_du_temps_sans_depasser_la_fin()
    {
        const double dt = 1.0 / 30;
        double debut = 0.25, fin = 0.6667, plafond = debut + (fin - debut) * 0.95, vitesse = (fin - debut) / 5;
        var affiche = debut;
        for (var t = 0.0; t < 2.5 - 1e-9; t += dt) affiche = ArcDeProgression.Suivant(affiche, debut, plafond, dt, vitesse);
        affiche.Should().BeApproximately(debut + vitesse * 2.5, 0.01, "à mi-étape, mi-chemin");
        for (var t = 0.0; t < 30; t += dt)
        {
            var suivant = ArcDeProgression.Suivant(affiche, debut, plafond, dt, vitesse);
            suivant.Should().BeLessThanOrEqualTo(plafond).And.BeGreaterThanOrEqualTo(affiche);
            affiche = suivant;
        }
    }

    [Fact]
    public void Apres_la_derniere_etape_la_jauge_glisse_a_cent_pour_cent_avant_le_masquage()
    {
        const double dt = 1.0 / 30;
        var affiche = 0.9;
        var temps = 0.0;
        while (affiche < 0.999 && temps < 5) { affiche = ArcDeProgression.Suivant(affiche, 1, 1, dt); temps += dt; }
        temps.Should().BeLessThan(1.1, "la jauge reste visible 1,1 s après la fin : elle doit avoir fini de glisser");
    }

    [Fact]
    public async Task A_la_fin_de_l_analyse_la_jauge_est_pleine_visible_puis_cachee_et_les_durees_sont_gardees()
    {
        var vm = Construire();
        double[]? gardees = null;
        double[] lues = [0.2, 0.2, 0.2, 2, 0.2, 0.2, 0.1, 0.4];
        vm.LireDureesDesEtapes = () => lues;
        vm.EnregistrerDureesDesEtapes = d => gardees = d;
        var bornesVues = new List<double>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ProgressionDuScan)) bornesVues.Add(vm.ProgressionDuScan); };

        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProgressionDuScan.Should().Be(1);
        vm.JaugeVisible.Should().BeTrue("juste après la fin, la jauge pleine reste à l'écran");
        bornesVues.Should().Contain(ScanViewModel.Bornes(lues, 4).Debut, "les durées lues pèsent sur la jauge");
        gardees.Should().NotBeNull().And.HaveCount(ScanViewModel.EtapesDuScan);
        gardees!.Should().OnlyContain(d => d > 0);

        // Le masquage part après 1,1 s ; un runner chargé peut le retarder (38 s mesurées sur GitHub), on attend jusqu'à 15 s.
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (vm.JaugeVisible && DateTime.UtcNow < limite) await Task.Delay(50);
        vm.JaugeVisible.Should().BeFalse();
    }

    private static ScanViewModel Construire()
    {
        var hw = new HardwareInventory(
            new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
            new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
            new[] { new MemoryModule("PN", "Kingston", 3800, 4000) },
            new MotherboardInfo("MSI", "B760"), new BiosInfo("1", DateTimeOffset.UtcNow, true), ChassisType.Desktop);
        var st = new SettingsState(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(hw));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "aucun jeu"));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(hw, st, new List<Finding>(), null)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k);
    }
}
