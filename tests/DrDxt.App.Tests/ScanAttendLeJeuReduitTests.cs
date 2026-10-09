using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// 26/09/2026 : un plein écran exclusif sur un seul écran est réduit dès qu'on clique sur DrDXT. Le scan ne
// trouvait jamais le jeu et disait « lance une partie » à un joueur en pleine partie. Il attend
// maintenant que le jeu revienne à l'écran, puis le mesure ; s'il reste réduit, la note le dit.
public sealed class ScanAttendLeJeuReduitTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3800, 4000), new MemoryModule("PN", "Kingston", 3800, 4000) },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static (ScanViewModel vm, IBenchmarkRunner br, List<bool> attentes) Build(IGameLocator locator)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(ci => ci.ArgAt<int>(0) > 0
              ? Result<BenchmarkResult>.Ok(bl)
              : Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "aucun jeu"));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), 68, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));

        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key, gameLocator: locator);
        // Chaque pas d'attente note si l'écran disait d'attendre à ce moment-là.
        var attentes = new List<bool>();
        vm.Patienter = _ => { attentes.Add(vm.AttenteDuJeu); return Task.CompletedTask; };
        return (vm, br, attentes);
    }

    [Fact]
    public async Task Le_joueur_revient_dans_son_jeu_et_c_est_ce_jeu_qui_est_mesure()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns((int?)null, (int?)null, (int?)null, 4242);
        locator.JeuOuvert().Returns(true);
        var (vm, br, attentes) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        await br.Received().RunAsync(4242, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Trois pas d'attente (le jeu revient au troisième), puis ceux de la surveillance de la capture.
        attentes.Should().HaveCountGreaterThanOrEqualTo(3);
        attentes.Take(3).Should().OnlyContain(a => a, "l'écran dit d'y retourner pendant toute l'attente");
        vm.AttenteDuJeu.Should().BeFalse("l'attente est finie");
        vm.DegradedNote.Should().BeNull();
    }

    // Une fenêtre qui sort de l'état réduit n'est, un instant, ni réduite ni à la taille de l'écran : un seul
    // relevé « pas ouvert » ne doit pas faire abandonner un jeu en train de revenir (vu sur le vrai Windows).
    [Fact]
    public async Task Un_releve_isole_en_pleine_restauration_n_abandonne_pas_l_attente()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns((int?)null, (int?)null, (int?)null, 4242);
        locator.JeuOuvert().Returns(true, false, true);
        var (vm, br, _) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        await br.Received().RunAsync(4242, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Un_jeu_ferme_pendant_l_attente_l_arrete_apres_quelques_releves()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns((int?)null);
        locator.JeuOuvert().Returns(true, false);
        var (vm, _, attentes) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        attentes.Should().HaveCount(ScanViewModel.AbsencesAvantAbandon, "pas 45 s pour un jeu fermé");
    }

    [Fact]
    public async Task Un_jeu_reste_reduit_la_note_le_dit_au_lieu_de_dire_de_lancer_une_partie()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns((int?)null);
        locator.JeuOuvert().Returns(true);
        var (vm, _, attentes) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        attentes.Should().HaveCount((int)ScanViewModel.DelaiAttenteDuJeu.TotalSeconds, "l'attente est bornée");
        vm.DegradedNote.Should().Be("Scan.Degraded.JeuReduit");
    }

    // La phrase est sur la vraie vue, sous « scan en cours », seulement pendant l'attente.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pendant_l_attente_la_vue_dit_de_retourner_dans_le_jeu(bool attente)
    {
        var (vm, _, _) = Build(Substitute.For<IGameLocator>());
        var textes = StaApplication.Run(() =>
        {
            vm.State = ScanState.Scanning;
            vm.AttenteDuJeu = attente;
            var vue = new DrDxt.App.Views.ScanView { DataContext = vm };
            vue.Measure(new System.Windows.Size(1240, 3000));
            vue.Arrange(new System.Windows.Rect(0, 0, 1240, 3000));
            vue.UpdateLayout();
            var vus = new List<string>();
            void Parcourir(System.Windows.DependencyObject d)
            {
                if (d is System.Windows.UIElement u && u.Visibility != System.Windows.Visibility.Visible) return;
                if (d is System.Windows.Controls.TextBlock t && !string.IsNullOrEmpty(t.Text)) vus.Add(t.Text);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    Parcourir(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(vue);
            return vus;
        });

        textes.Should().Contain("Scan.State.Scanning", "temoin : la zone du scan en cours est rendue");
        if (attente) textes.Should().Contain("Scan.AttenteJeu");
        else textes.Should().NotContain("Scan.AttenteJeu");
    }

    [Fact]
    public void La_phrase_d_attente_dit_la_duree_de_la_capture()
    {
        var vm = ScanViewModelTestSeams.Build(
            Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(), Substitute.For<IBenchmarkRunner>(),
            Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), key => key == "Scan.AttenteJeu" ? "reste {0} s" : key);

        vm.LabelAttenteDuJeu.Should().Be($"reste {ScanViewModel.DureeCaptureEnJeu.TotalSeconds} s");
        ScanViewModel.DureeCaptureEnJeu.Should().BeGreaterThan(TimeSpan.Zero);
    }

    // Le joueur revient sur DrDXT au milieu de la capture : le jeu est réduit, ses images ne décrivent plus la
    // partie. Aucun chiffre n'est donné, et la note dit pourquoi.
    [Fact]
    public async Task Quitter_le_jeu_pendant_la_capture_ne_donne_aucun_chiffre()
    {
        // 06/10/2026 : 4242 rendait ce test instable : la note nomme le programme dont c'est le numéro (NomExeDuProcessus lit le vrai
        // Windows), et un processus 4242 existe parfois, surtout pendant une compilation. 07/10/2026 : 4243 aussi, car Windows
        // ignore les deux bits du bas d'un numéro de processus (4243 ouvre 4240). Un numéro hors de portée des vrais
        // processus ne nomme jamais rien.
        const int Absent = int.MaxValue - 3;
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns(Absent, Absent, Absent, (int?)null);
        locator.JeuOuvert().Returns(true);
        var (vm, br, _) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        await br.Received().RunAsync(Absent, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        vm.DegradedNote.Should().Be("Scan.Degraded.JeuQuitte");
        vm.State.Should().Be(ScanState.Degraded, "un chiffre mêlé à un jeu réduit n'est pas affiché");
    }

    // 04/10/2026 (plan 1.5) : la note nomme le programme pris pour le jeu ; un faux positif se lit tout de suite.
    [Fact]
    public async Task Quitter_le_jeu_nomme_le_programme_retenu()
    {
        var moi = Environment.ProcessId;
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns(moi, moi, moi, (int?)null);
        locator.JeuOuvert().Returns(true);
        var (vm, _, _) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.DegradedNote.Should().Be("Scan.Degraded.JeuQuitteNomme");
    }

    // Deux écrans : le joueur clique un stream en plein écran, qui passe au premier plan ; son jeu reste
    // affiché sur l'autre écran. La capture est valide et doit être gardée.
    [Fact]
    public async Task Un_autre_plein_ecran_au_premier_plan_n_ecarte_pas_la_capture()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns(4242, 4242, 7777);
        locator.EstAffiche(4242).Returns(true);
        locator.JeuOuvert().Returns(true);
        var (vm, _, _) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.DegradedNote.Should().BeNull();
        vm.State.Should().NotBe(ScanState.Degraded);
    }

    [Fact]
    public async Task Rester_en_jeu_pendant_la_capture_garde_le_chiffre()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns(4242);
        locator.JeuOuvert().Returns(true);
        var (vm, _, _) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.DegradedNote.Should().BeNull();
        vm.State.Should().NotBe(ScanState.Degraded, "temoin : la même capture, jeu resté à l'écran, garde son chiffre");
    }

    [Fact]
    public async Task Aucun_jeu_ouvert_pas_d_attente_et_la_phrase_d_avant()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns((int?)null);
        locator.JeuOuvert().Returns(false);
        var (vm, _, attentes) = Build(locator);

        await vm.ScanCommand.ExecuteAsync(null);

        attentes.Should().BeEmpty();
        vm.DegradedNote.Should().Be("Scan.Degraded.PresentMon");
    }
}
