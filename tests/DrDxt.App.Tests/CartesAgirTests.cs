using DrDxt.Tests.Shared;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Les cartes « Agir ». Leurs etats « rendu » et
/// « allume » ne se photographient pas sans ecrire sur la machine : ils se prouvent ici, avec un
/// helper factice qui enregistre ce qu'on lui demande.
/// </summary>
public sealed partial class CartesAgirTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static ScanViewModel Vm(IReadOnlyList<RepairFinding> audit, IElevatedClient? helper,
        Func<string, string, string>? writeReport = null, DrDxt.Measurement.Cpu.CpuBaselineService? cpu = null,
        Func<string, string>? loc = null, int? jeu = null, DrDxt.Measurement.PerformanceJournalStore? journal = null,
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null,
        DrDxt.Application.Tweaks.Hardware.IRegistryReader? registreGains = null,
        Func<(InstabiliteGpuObservee?, IReadOnlyList<ArretClasse>?)>? lireStabilite = null, IDesiredStateStore? manifeste = null)
    {
        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.LocateRunningGamePid().Returns(jeu);
        locator.JeuOuvert().Returns(jeu is not null);
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
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, loc ?? (k => k), repairAudit: () => audit, helper: helper, writeReport: writeReport,
            machineId: cpu is null ? null : () => "m1", cpuBaseline: cpu, gameLocator: locator, performanceJournal: journal,
            journalLeviers: journalLeviers, registreGains: registreGains, lireStabilite: lireStabilite, manifeste: manifeste);
    }

    private static IElevatedClient Helper(Result reponse)
    {
        var h = Substitute.For<IElevatedClient>();
        h.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(reponse));
        // Un faux NSubstitute rend un autre faux pour un membre par défaut d'interface : le client sans relance (battements,
        // plafond d'envoi) est ici le même faux, comme pour un vrai client qui ne lance rien.
        h.SansRelance.Returns(h);
        return h;
    }

    // Un exe qu'aucune machine ne fait tourner : le mode s'allume sans jamais poser le plafond.
    private const string JeuAbsent = "drdxt-jeu-absent-des-tests.exe";

    // La veille du ping. Sans scan en partie saturee, la carte n'offrait rien a faire.
    private static IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi> Fenetre(double pingEnCharge)
        => Enumerable.Range(0, 25).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 10))
            .Concat(Enumerable.Range(0, 25).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(100, pingEnCharge))).ToList();

    private sealed class PrioritesFactices : DrDxt.App.Platform.IPrioritesDeProcessus
    {
        public System.Diagnostics.ProcessPriorityClass? Lire(int pid) => null;
        public bool Poser(int pid, System.Diagnostics.ProcessPriorityClass priorite) => false;
    }

    // Une sous-tension AMD que le pilote a gardée au redémarrage : rendre l'origine dépasse la bande du helper. Sur « Annuler »,
    // la carte est remise à l'usine et le réglage quitte le journal ; la garde d'incident, qui rend sans clic, dit le refus.
    private static (IElevatedClient Helper, ITweak Levier) CarteQuiRefuseLOrigine()
    {
        var helper = Substitute.For<IElevatedClient>();
        helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        var levier = Substitute.For<ITweak>();
        levier.RevertAsync(Arg.Any<TweakSnapshot>())
            .Returns(Result.Fail(ErrorCodes.GpuRemiseUsineRequise, "au-delà de la bande"));
        return (helper, levier);
    }

    // La référence processeur de la machine de référence (11/09 17:46, ±7,4 %) précède de cinq minutes
    // une capture de Warzone. Écrite avant le garde, elle n'a pas le champ JeuFermeVerifie : la ligne le dit.
    private sealed class JournalCpu : DrDxt.Measurement.IPerformanceJournalFile
    {
        private string? _contenu;
        public Result WriteAtomic(string json) { _contenu = json; return Result.Ok(); }
        public Result<string?> Read() => Result<string?>.Ok(_contenu);
    }

    // La remesure sans geste. Une reference suspecte se refait seule quand le poste est au repos.
    private static Func<CancellationToken, Task<(TimeSpan SansSaisie, double? ChargeCpu)>> Poste(TimeSpan sansSaisie, double? charge)
        => _ => Task.FromResult((sansSaisie, charge));

    private static readonly DateTimeOffset Maintenant = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
}
