using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.App.Tests;

/// <summary>
/// « LIRE AUSSI CE QUI DEMANDE L'ADMINISTRATEUR ».
///
/// Demande produit du 16/09/2026 : pousser les lectures plus loin pendant les tests. Le produit ne
/// demandait l'UAC que pour ECRIRE, donc la latence des pilotes, le BCD et les incidents noyau
/// restaient « non lus » dans l'appli. Le bouton relance DrDXT eleve pour lire, il n'ecrit rien.
/// </summary>
public class RelanceAdministrateurTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "560.0", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static ViewModels.ScanViewModel Vm(bool? estEleve, Func<bool>? relancer)
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
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), st, new List<Finding>(), 90, bl,
              new ScoreBreakdown(90, 90, 90), new DimensionStatus(false, false))));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));

        IReadOnlyList<RepairFinding> Audit() => new[]
        {
            new RepairFinding("LATENCE", "DPC/ISR des pilotes", null, null, RepairStatus.Unknown,
                "une session noyau exige l'administrateur, et l'App n'en a pas les droits"),
        };

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k, repairAudit: Audit,
            estEleve: estEleve, relancerEnAdministrateur: relancer);
    }

    [Fact]
    public async Task Sans_droits_et_avec_des_angles_morts_le_bouton_est_propose()
    {
        var vm = Vm(estEleve: false, relancer: () => true);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PeutRelancerEnAdministrateur.Should().BeTrue();
    }

    [Fact]
    public async Task Deja_administrateur_le_bouton_n_existe_pas()
    {
        var vm = Vm(estEleve: true, relancer: () => true);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PeutRelancerEnAdministrateur.Should().BeFalse("les lectures ont deja eu les droits");
    }

    [Fact]
    public async Task Droits_inconnus_le_bouton_ne_promet_rien()
    {
        var vm = Vm(estEleve: null, relancer: () => true);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PeutRelancerEnAdministrateur.Should().BeFalse();
    }

    [Fact]
    public async Task Un_accord_refuse_se_dit_et_l_appli_reste_la()
    {
        var appels = 0;
        var vm = Vm(estEleve: false, relancer: () => { appels++; return false; });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.RelancerEnAdministrateurCommand.Execute(null);

        appels.Should().Be(1);
        vm.MessageRelanceAdministrateur.Should().Be("Scan.Elevation.Refused");
    }

    // 03/10/2026 (boucle 2D2P, D6.2) : « aucun intérêt de faire un scan sans puis un scan avec admin, faut tout faire
    // direct ». Sans droits, lancer l'analyse demande l'élévation une fois et confie l'analyse à l'instance élevée.
    [Fact]
    public async Task Sans_droits_lancer_l_analyse_la_confie_a_l_instance_elevee_avec_la_scene()
    {
        var vm = Vm(estEleve: false, relancer: () => true);
        int? sceneDemandee = null;
        vm.RelancerEleveEtAnalyser = scene => { sceneDemandee = scene; return true; };
        vm.SceneDeclaree = vm.ChoixDeScene[1];

        await vm.ScanCommand.ExecuteAsync(null);

        sceneDemandee.Should().Be(1, "la scène choisie suit la relance");
        vm.State.Should().Be(ViewModels.ScanState.Idle, "rien n'est analysé deux fois : l'instance élevée s'en charge");
    }

    [Fact]
    public async Task Elevation_refusee_l_analyse_se_fait_quand_meme_le_dit_et_ne_redemande_pas()
    {
        var vm = Vm(estEleve: false, relancer: () => true);
        var demandes = 0;
        vm.RelancerEleveEtAnalyser = _ => { demandes++; return false; };

        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().NotBe(ViewModels.ScanState.Idle, "l'analyse a tourné sans droits");
        vm.MessageRelanceAdministrateur.Should().NotBeNullOrEmpty("le refus est dit");

        await vm.ScanCommand.ExecuteAsync(null);
        demandes.Should().Be(1, "une seule demande, pas à chaque analyse");
    }

    [Fact]
    public async Task Deja_administrateur_aucune_relance()
    {
        var vm = Vm(estEleve: true, relancer: () => true);
        var demandes = 0;
        vm.RelancerEleveEtAnalyser = _ => { demandes++; return true; };

        await vm.ScanCommand.ExecuteAsync(null);

        demandes.Should().Be(0);
        vm.State.Should().NotBe(ViewModels.ScanState.Idle);
    }
}
