using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// L'écran principal ne montre que ce qui demande une action ou signale un problème : l'information d'expert sans problème
// se replie sous « Détails techniques (N) », par page. Le rapport exporté, lui, garde tout.
public sealed class DetailsTechniquesEcranTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"details-{Guid.NewGuid():N}.json");
    private readonly DrDxt.Core.FichierJsonAtomique _avant = ScanViewModel.FichierDetailsDeplies;

    public DetailsTechniquesEcranTests() => ScanViewModel.FichierDetailsDeplies = new(_fichier, "DETAILS_TECHNIQUES");

    public void Dispose()
    {
        ScanViewModel.FichierDetailsDeplies = _avant;
        try { File.Delete(_fichier); } catch (IOException) { }
    }

    private static readonly RepairFinding Veille = new("CPU", "etats de veille", "C1 2 %", null, RepairStatus.Observed, "lu au repos.");
    private static readonly RepairFinding Caches = new("CPU", "caches L3", "33 Mo", null, RepairStatus.Observed, "un cache.");
    private static readonly RepairFinding AmdProfond = new("CPU", "lectures AMD profondes", null, null, RepairStatus.Unknown, "module absent.");
    private static readonly RepairFinding Frequence = new("CPU", "frequence effective", "4475 MHz", null, RepairStatus.Observed, "mesurée.");

    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static ScanViewModel Vm(IReadOnlyList<RepairFinding> audit, Func<string, string, string>? writeReport = null)
    {
        var st = new SettingsState(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On,
            Array.Empty<int>(), TriState.Unknown);
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result<BenchmarkResult>.Ok(bl));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), st, new List<Finding>(), bl)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k, repairAudit: () => audit, writeReport: writeReport);
    }

    [Fact]
    public async Task Un_constat_d_expert_sans_probleme_va_dans_les_details_compte_juste()
    {
        var vm = Vm([Veille, Caches, AmdProfond, Frequence]);
        await vm.ScanCommand.ExecuteAsync(null);

        var cpu = vm.Domaines.Single(d => d.Cle == "processeur");
        cpu.Details.Should().HaveCount(2, "états de veille et caches L3");
        cpu.Details.Should().Contain(l => l.Texte.Contains("C1 2 %"));
        cpu.Mesures.Should().Contain(m => m.Contains("4475 MHz"), "témoin : une lecture hors des familles reste à l'écran");
        cpu.Mesures.Should().NotContain(m => m.Contains("C1 2 %") || m.Contains("33 Mo"));
        vm.RepairBlindSpots.Should().Contain(r => r.NomCle == "lectures AMD profondes", "un non-lu reste dans « Pas pu lire »");
        vm.DetailsTechniquesDeplies.Should().BeFalse("replié par défaut");
    }

    // Le titre et le nom accessible portent le compte, au format de la langue : « Détails techniques (2) ».
    [Fact]
    public void Le_titre_des_details_porte_son_compte()
    {
        var details = new Dictionary<string, IReadOnlyList<LigneMesure>> { ["processeur"] = [new("a", null), new("b", null)] };
        var cpu = CarteDeLaMachine.Construire([], new Dictionary<string, IReadOnlyList<string>>(), [],
            k => k == "Carte.DetailsTechniques" ? "Détails techniques ({0})" : k, details: details).Single(d => d.Cle == "processeur");
        cpu.TitreDesDetails.Should().Be("Détails techniques (2)");
        cpu.Etat.Should().Be(EtatDomaine.RienDeSignale, "une page qui n'a que des détails a bien été lue");
    }

    [Fact]
    public async Task Le_meme_constat_suspect_reste_en_haut()
    {
        var vm = Vm([Veille with { Statut = RepairStatus.Suspicious }]);
        await vm.ScanCommand.ExecuteAsync(null);

        var cpu = vm.Domaines.Single(d => d.Cle == "processeur");
        cpu.Details.Should().BeEmpty();
        cpu.Lignes.Should().ContainSingle(l => l.Valeur == "C1 2 %");
    }

    [Fact]
    public async Task Le_rapport_exporte_garde_tous_les_constats()
    {
        string? ecrit = null;
        var vm = Vm([Veille, Caches, AmdProfond, Frequence], (_, texte) => { ecrit = texte; return "rapport.txt"; });
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().NotBeNull();
        ecrit.Should().Contain("C1 2 %").And.Contain("33 Mo").And.Contain("4475 MHz");
        ecrit.Should().Contain(RepairLibelles.Ligne("CPU", "lectures AMD profondes"));
    }

    [Fact]
    public void Deplie_ou_non_se_garde_d_un_lancement_a_l_autre()
    {
        var vm = Vm([]);
        vm.DetailsTechniquesDeplies = true;
        File.ReadAllText(_fichier).Trim().Should().Be("true");
        Vm([]).DetailsTechniquesDeplies.Should().BeTrue("un nouveau lancement relit le choix");
    }
}
