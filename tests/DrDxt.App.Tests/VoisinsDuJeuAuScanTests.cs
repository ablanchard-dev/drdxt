using System.Collections.Generic;
using System.Threading;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 11/09/2026, demande produit — l'ecran dit ce qui tournait a cote du jeu.
//
// Le fait qui l'a declenchee : un scan a affiche « 164 FPS » pendant qu'on regardait l'ecran de
// DrDXT, le jeu en arriere-plan. Le meme soir, en partie reelle avec nos compilations sur son
// processeur : 79,5 FPS, 1 % low 23,7. Le meme produit, la meme machine, deux chiffres qui ne
// veulent rien dire l'un sans l'autre — parce que rien ne disait ce qu'il y avait autour.
//
// Ce que ces tests tiennent :
//   * un voisin notable arrive jusqu'a l'ecran, avec son nom lisible et ses parts ;
//   * rien vu (ou rien lu) ⇒ liste vide et phrase qui le dit — jamais « rien ne tournait » ;
//   * un lecteur qui jette ne casse pas le scan.
// -----------------------------------------------------------------------
public class VoisinsDuJeuAuScanTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 3800, 3800) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(TriState.On, TriState.On,
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private sealed class VoisinsFactices(params VoisinDuJeu[] vus) : ILecteurDeVoisins
    {
        public IReadOnlyList<VoisinDuJeu> Lire(int? pidDuJeu, CancellationToken ct) => vus;
    }

    private sealed class VoisinsQuiJettent : ILecteurDeVoisins
    {
        public IReadOnlyList<VoisinDuJeu> Lire(int? pidDuJeu, CancellationToken ct)
            => throw new InvalidOperationException("compteurs indisponibles");
    }

    private static ScanViewModel Build(ILecteurDeVoisins? voisins)
    {
        var baseline = new BenchmarkResult(142.7, 98.3, 75.0, new List<double> { 7.0 }, null, 2.1, 5);

        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(baseline));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), 72, baseline,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        // Les VRAIS gabarits de la ressource : sans eux, un localisateur identite rendrait la CLE et
        // le test ne verrait jamais si le nom du voisin arrive dans la ligne.
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key switch
        {
            "Scan.Voisins.Ligne" => "{0} - {1:0.#} % de processeur (d'un coeur)",
            "Scan.Voisins.LigneGpu" => "{0} - {1:0.#} % de processeur (d'un coeur), {2:0.#} % de carte graphique",
            _ => key,
        }, voisins: voisins);
    }

    [Fact]
    public async Task Ce_qui_tournait_a_cote_arrive_jusqu_a_l_ecran()
    {
        var vm = Build(new VoisinsFactices(
            new VoisinDuJeu("obs64", 92.3, 26.1),
            new VoisinDuJeu("dotnet", 380.5, null)));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.VoisinsConnus.Should().BeTrue();
        vm.VoisinsDuJeu.Should().HaveCount(2);
        var lignes = string.Join(" | ", vm.VoisinsDuJeu);
        lignes.Should().Contain("obs64").And.Contain("dotnet");
        lignes.Should().Contain("26", "la part de carte graphique est dite quand on sait la lire");
        vm.VoisinsDuJeu[1].Should().NotContain("carte graphique",
            "un voisin dont on ne lit pas la part GPU ne doit pas en afficher une");
    }

    [Fact]
    public async Task Rien_vu_se_dit_par_une_phrase_jamais_par_le_silence()
    {
        var vm = Build(new VoisinsFactices());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.VoisinsDuJeu.Should().BeEmpty();
        vm.VoisinsConnus.Should().BeFalse();
        // Le harnais ne lance aucun jeu : la phrase est celle « sans jeu » (17/09/2026), jamais le silence.
        vm.VoisinsResume.Should().Be("Scan.Voisins.AucunSansJeu",
            "une liste vide doit DIRE qu'on n'a rien vu (ou rien su lire), pas laisser croire que rien ne tournait");
    }

    [Fact]
    public async Task Un_lecteur_de_voisins_qui_jette_ne_casse_pas_le_scan()
    {
        var vm = Build(new VoisinsQuiJettent());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.State.Should().Be(ScanState.Done);
        vm.VoisinsConnus.Should().BeFalse();
        // 03/10/2026 (D8.6) : un lecteur qui jette n'a pas regardé ; l'écran le dit, il ne dit pas « rien vu ».
        vm.VoisinsResume.Should().Be("Scan.Voisins.NonLus");
    }

    [Fact]
    public async Task Un_lecteur_qui_n_a_pas_pu_regarder_ne_dit_pas_rien_vu()
    {
        var vm = Build(new AucunVoisin());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.VoisinsResume.Should().Be("Scan.Voisins.NonLus", "null = pas pu regarder, jamais « rien de notable »");
    }

    private sealed class VoisinsComptes : ILecteurDeVoisins
    {
        public int Appels;
        public IReadOnlyList<VoisinDuJeu> Lire(int? pidDuJeu, CancellationToken ct)
        {
            Interlocked.Increment(ref Appels);
            return Array.Empty<VoisinDuJeu>();
        }
    }

    // 11/09/2026 — la borne de cout. Cette lecture tourne pendant que le joueur joue : 600 ms
    // d'attente et deux enumerations de processus. Mesuree au banc `tools/voisinscout` a 39 ms de
    // processeur par appel, soit 0,195 % d'UN cœur sur un scan de 20 s — negligeable une fois,
    // inacceptable en boucle. Ce test tient le « une fois » : remettre un appel par echantillon de
    // frames le fait rougir.
    [Fact]
    public async Task Ce_qui_tourne_a_cote_se_lit_UNE_fois_par_scan_jamais_en_boucle()
    {
        var compteur = new VoisinsComptes();
        var vm = Build(compteur);

        await vm.ScanCommand.ExecuteAsync(null);

        compteur.Appels.Should().Be(1,
            "la mesure des voisins coute un cœur pendant la partie : une fois par scan, jamais dans la boucle de mesure");
    }

    // Le calcul de la part GPU, isole. Avant le 11/09 il lisait un seul echantillon d'un compteur
    // cumulatif : il rendait toujours 0, au prix de 552 ms de processeur. Ici la difference de deux
    // cumuls sur une duree connue doit rendre le vrai pourcentage.
    [Fact]
    public void La_part_de_carte_graphique_vient_de_la_DIFFERENCE_de_deux_lectures()
    {
        var duree = TimeSpan.FromMilliseconds(600);
        // Le moteur 3D de 42 a travaille 150 ms sur 600 (25 %) ; 7 n'existait pas avant.
        var debut = new Dictionary<string, long> { [Moteur(42, "3D")] = 1_000_000, [Moteur(99, "3D")] = 500 };
        var fin = new Dictionary<string, long>
        {
            [Moteur(42, "3D")] = 1_000_000 + TimeSpan.FromMilliseconds(150).Ticks, [Moteur(7, "3D")] = 12,
        };

        var parts = VoisinsWin32.PartsGpu(debut, fin, duree);

        parts[42].Should().BeApproximately(25.0, 0.01);
        parts.Should().NotContainKey(7, "un processus absent de la premiere lecture n'a pas de part mesurable");
    }

    // Le Gestionnaire des taches montre le moteur le plus occupe, pas la somme des moteurs
    // (devblogs.microsoft.com/directx/gpus-in-the-task-manager). Un logiciel de diffusion qui rend en 3D
    // a 25 % et encode la video a 60 % occupe le GPU a 60 %, pas a 85 %.
    [Fact]
    public void La_part_de_carte_graphique_est_celle_du_moteur_le_plus_occupe_pas_la_somme()
    {
        var duree = TimeSpan.FromMilliseconds(600);
        var debut = new Dictionary<string, long> { [Moteur(42, "3D")] = 0, [Moteur(42, "VideoEncode")] = 0 };
        var fin = new Dictionary<string, long>
        {
            [Moteur(42, "3D")] = TimeSpan.FromMilliseconds(150).Ticks,
            [Moteur(42, "VideoEncode")] = TimeSpan.FromMilliseconds(360).Ticks,
        };

        VoisinsWin32.PartsGpu(debut, fin, duree)[42].Should().BeApproximately(60.0, 0.01);
    }

    [Fact]
    public void Sans_compteurs_lisibles_aucune_part_de_carte_graphique_n_est_inventee()
    {
        VoisinsWin32.PartsGpu(new Dictionary<string, long>(), new Dictionary<string, long> { [Moteur(1, "3D")] = 5 },
            TimeSpan.FromMilliseconds(600)).Should().BeEmpty();
    }

    // Le nom d'instance tel que Windows l'ecrit dans « GPU Engine ».
    private static string Moteur(int pid, string type) => $"pid_{pid}_luid_0x00000000_0x0000D1F2_phys_0_eng_0_engtype_{type}";

    [Fact]
    public async Task Sans_couture_de_voisins_le_scan_se_comporte_comme_avant()
    {
        var vm = Build(voisins: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.VoisinsDuJeu.Should().BeEmpty();
        vm.VoisinsConnus.Should().BeFalse();
    }

    // 03/10/2026 (D10.3, revue n° 4) : la phrase de la carte finissait par « : » suivi de rien (la liste est repliée).
    [Theory]
    [InlineData("Scan.Voisins.Header", "fr")]
    [InlineData("Scan.Voisins.HeaderSansJeu", "fr")]
    [InlineData("Scan.Voisins.Header", "en")]
    [InlineData("Scan.Voisins.HeaderSansJeu", "en")]
    public void La_phrase_de_la_carte_est_complete(string cle, string langue) =>
        new DrDxt.Infrastructure.Localization.ResxLocalizer().Get(cle, new System.Globalization.CultureInfo(langue))
            .TrimEnd().Should().EndWith(".").And.NotEndWith(":");
}
