using System.Diagnostics;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La boucle « programmes gourmands après le jeu ». Système simulé : aucune priorité n'est
// posée sur la machine des tests. Le jeu tourne deux lectures, puis s'arrête : le voisin calmé doit retrouver sa priorité.
public sealed class CalmerPendantLesPartiesTests
{
    private const int Jeu = 100, Obs = 300;

    private sealed class Systeme : IPrioritesDeProcessus
    {
        public Dictionary<int, ProcessPriorityClass> Priorites { get; } = new() { [Obs] = ProcessPriorityClass.Normal };
        public List<ProcessPriorityClass> Historique { get; } = [];
        public ProcessPriorityClass? Lire(int pid) => Priorites.TryGetValue(pid, out var p) ? p : null;
        public bool Poser(int pid, ProcessPriorityClass p) { Priorites[pid] = p; Historique.Add(p); return true; }
    }

    private sealed class ObsGourmand : ILecteurDeVoisins
    {
        public IReadOnlyList<VoisinDuJeu> Lire(int? pidDuJeu, CancellationToken ct) => [new("obs64", 60, null, Obs)];
    }

    private static (ScanViewModel vm, Systeme sys, CancellationTokenSource fin) Build(params int?[] pidsSuccessifs)
    {
        var locator = Substitute.For<IGameLocator>();
        var fin = new CancellationTokenSource();
        var i = 0;
        locator.LocateRunningGamePid().Returns(_ =>
        {
            if (i >= pidsSuccessifs.Length) { fin.Cancel(); return pidsSuccessifs[^1]; }
            return pidsSuccessifs[i++];
        });
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(),
            k => k == "Voisins.Calmer.Fait" ? "calmés : {0}" : k,
            gameLocator: locator, voisins: new ObsGourmand());
        var sys = new Systeme();
        vm.Priorites = sys;
        vm.PasCalmer = TimeSpan.Zero;
        vm.PidPremierPlan = () => 0;
        vm.EstUnJeu = _ => true;
        return (vm, sys, fin);
    }

    [Fact]
    public async Task Le_voisin_calme_pendant_la_partie_retrouve_sa_priorite_quand_elle_finit()
    {
        var (vm, sys, fin) = Build(Jeu, Jeu, Jeu, null);

        await vm.CalmerPendantLesPartiesAsync(fin.Token);

        sys.Historique.Should().Equal(ProcessPriorityClass.BelowNormal, ProcessPriorityClass.Normal);
        sys.Priorites[Obs].Should().Be(ProcessPriorityClass.Normal);
        vm.VoisinsCalmes.Should().Contain("obs64");
    }

    [Fact]
    public async Task Arreter_en_pleine_partie_rend_aussi_la_priorite()
    {
        var (vm, sys, fin) = Build(Jeu, Jeu, Jeu);

        await vm.CalmerPendantLesPartiesAsync(fin.Token);

        sys.Priorites[Obs].Should().Be(ProcessPriorityClass.Normal, "l'arrêt rend tout, même sans fin de partie");
        sys.Historique.Should().Contain(ProcessPriorityClass.BelowNormal);
    }

    // Une vidéo en plein écran n'est pas une partie ; rien n'est calmé à côté d'elle.
    [Fact]
    public async Task Une_fenetre_qui_n_est_pas_un_jeu_ne_calme_rien()
    {
        var (vm, sys, fin) = Build(Jeu, Jeu, Jeu, null);
        vm.EstUnJeu = _ => false;

        await vm.CalmerPendantLesPartiesAsync(fin.Token);

        sys.Historique.Should().BeEmpty();
    }

    // Une lecture des voisins qui ne finit qu'au signal du test : la boucle reste suspendue au milieu d'une partie.
    private sealed class ObsBloque : ILecteurDeVoisins
    {
        public readonly TaskCompletionSource Bloquee = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Liberer = new();
        private int _appels;
        public IReadOnlyList<VoisinDuJeu> Lire(int? pidDuJeu, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _appels) == 3) { Bloquee.TrySetResult(); Liberer.Wait(TimeSpan.FromSeconds(10)); }
            return [new("obs64", 60, null, Obs)];
        }
    }

    // Deux lectures abaissent OBS, la troisième reste suspendue ; le joueur éteint puis rallume, et la seconde boucle ne voit
    // plus de jeu. La première boucle doit rendre ce que SON calmeur a abaissé.
    private static (ScanViewModel vm, Systeme sys, ObsBloque lecteur, Func<bool> phase2) BuildBloque()
    {
        var locator = Substitute.For<IGameLocator>();
        var phase2 = false;
        locator.LocateRunningGamePid().Returns(_ => phase2 ? null : Jeu);
        var lecteur = new ObsBloque();
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            gameLocator: locator, voisins: lecteur);
        var sys = new Systeme();
        vm.Priorites = sys;
        vm.PasCalmer = TimeSpan.FromMilliseconds(10);
        vm.PidPremierPlan = () => 0;
        vm.EstUnJeu = _ => true;
        return (vm, sys, lecteur, () => phase2 = true);
    }

    [Fact]
    public async Task Eteindre_puis_rallumer_pendant_une_lecture_rend_ce_que_la_premiere_boucle_a_abaisse()
    {
        var (vm, sys, lecteur, phase2) = BuildBloque();
        using var premiere = new CancellationTokenSource();
        var boucle1 = vm.CalmerPendantLesPartiesAsync(premiere.Token);
        await lecteur.Bloquee.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sys.Priorites[Obs].Should().Be(ProcessPriorityClass.BelowNormal, "témoin : la première boucle a abaissé OBS");

        premiere.Cancel();
        phase2();
        using var seconde = new CancellationTokenSource();
        var boucle2 = vm.CalmerPendantLesPartiesAsync(seconde.Token);
        lecteur.Liberer.Set();
        await boucle1.WaitAsync(TimeSpan.FromSeconds(10));
        seconde.Cancel();
        await boucle2.WaitAsync(TimeSpan.FromSeconds(10));

        sys.Priorites[Obs].Should().Be(ProcessPriorityClass.Normal);
    }

    // La fermeture de l'App pendant la lecture suspendue rend tout, quelle que soit la boucle qui l'a abaissé, et rien n'est
    // abaissé après.
    [Fact]
    public async Task Fermer_l_app_pendant_une_lecture_suspendue_rend_tout()
    {
        var (vm, sys, lecteur, phase2) = BuildBloque();
        using var premiere = new CancellationTokenSource();
        var boucle1 = vm.CalmerPendantLesPartiesAsync(premiere.Token);
        await lecteur.Bloquee.Task.WaitAsync(TimeSpan.FromSeconds(10));
        premiere.Cancel();
        phase2();
        using var seconde = new CancellationTokenSource();
        var boucle2 = vm.CalmerPendantLesPartiesAsync(seconde.Token);

        vm.RendreLesPriorites();

        sys.Priorites[Obs].Should().Be(ProcessPriorityClass.Normal, "rendu à la fermeture, la lecture encore suspendue");
        lecteur.Liberer.Set();
        await boucle1.WaitAsync(TimeSpan.FromSeconds(10));
        seconde.Cancel();
        await boucle2.WaitAsync(TimeSpan.FromSeconds(10));
        sys.Priorites[Obs].Should().Be(ProcessPriorityClass.Normal);
    }

    [Fact]
    public void Coupe_par_defaut_rien_ne_tourne()
    {
        var (vm, sys, _) = Build(Jeu, Jeu, Jeu);
        vm.CalmerVoisinsActif.Should().BeFalse();
        vm.RallumerCalmerVoisins();
        vm.CalmerVoisinsActif.Should().BeFalse("aucun choix gardé : rien ne s'allume tout seul");
        sys.Historique.Should().BeEmpty();
    }
}
