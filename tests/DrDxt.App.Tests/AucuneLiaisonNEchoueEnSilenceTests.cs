using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DrDxt.App.ViewModels;
using DrDxt.App.Views;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Une liaison WPF qui echoue ne leve rien. Elle ecrit une ligne dans une trace
// que personne ne lit, laisse la zone vide, et l'application a l'air normale.
// C'est le defaut silencieux le plus courant d'une interface XAML : un nom de
// propriete renomme d'un cote et pas de l'autre, et un chiffre disparait de la
// vitrine sans qu'aucun test ne rougisse.
//
// Le navigateur a sa console ; ici, l'equivalent est
// `PresentationTraceSources.DataBindingSource`. Ce test l'ecoute pendant que la
// vue se realise vraiment (Measure + Arrange + file du Dispatcher videe), et
// refuse la moindre erreur de liaison.
//
// Il ne remplace pas un oeil humain sur le rendu : il garantit seulement que ce
// que le XAML demande existe bel et bien.
// -----------------------------------------------------------------------
[Collection(CollectionWpf.Nom)]
public sealed class AucuneLiaisonNEchoueEnSilenceTests
{
    private sealed class EcouteDesLiaisons : TraceListener
    {
        public List<string> Erreurs { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Erreurs.Add(message!);
        }
    }

    private static List<string> ErreursEnRealisant(Func<FrameworkElement> construire, double largeur, double hauteur)
        => StaApplication.Run(() =>
        {
            var ecoute = new EcouteDesLiaisons();
            var niveau = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(ecoute);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                var vue = construire();
                vue.Measure(new Size(largeur, hauteur));
                vue.Arrange(new Rect(0, 0, largeur, hauteur));
                vue.UpdateLayout();
                // Les liaisons se resolvent en file d'attente : sans ce vidage, une
                // erreur arrivee apres Arrange ne serait jamais vue.
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                return ecoute.Erreurs.ToList();
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(ecoute);
                PresentationTraceSources.DataBindingSource.Switch.Level = niveau;
            }
        });

    [Fact]
    public void La_promesse_du_produit_se_lit_avant_le_premier_scan()
    {
        // « Ce que DrDXT promet » (trouver, prouver, ne rien casser, survivre aux mises à
        // jour, dire aussi quand la machine est déjà bonne) doit être affiché, pas seulement traduit.
        // Elle tient lieu de sous-titre de l'accueil : elle le contient déjà, sans rien répéter.
        var textes = StaApplication.Run(() =>
        {
            var vm = ScanCharge();
            vm.State = ScanState.Idle;
            var vue = new ScanView { DataContext = vm };
            vue.Measure(new Size(1240, 900));
            vue.Arrange(new Rect(0, 0, 1240, 900));
            vue.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            // IsVisible reste faux hors fenêtre : on suit Visibility jusqu'à la racine, en élaguant
            // toute branche repliée (l'état « résultat » est dans l'arbre, mais caché).
            var trouves = new List<string>();
            void Parcourir(DependencyObject d)
            {
                if (d is UIElement u && u.Visibility != Visibility.Visible) return;
                if (d is TextBlock t && !string.IsNullOrEmpty(t.Text)) trouves.Add(t.Text);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    Parcourir(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(vue);
            trouves.Should().Contain("UI.Idle.HeadlineLead UI.Idle.HeadlineAccent", "témoin : l'accueil est bien celui qu'on lit");
            return trouves;
        });

        textes.Should().Contain("Product.Promise");
        textes.Should().NotContain("UI.Idle.Subtitle", "le sous-titre redisait la moitié de la promesse");
    }

    // ---- L'ecran de scan : celui que le client regarde le plus longtemps ----
    [Theory]
    [InlineData(1240, 900)]
    [InlineData(900, 640)]
    public void L_ecran_de_scan_au_repos_ne_laisse_aucune_liaison_en_erreur(double largeur, double hauteur)
    {
        // Au REPOS, avant tout scan : c'est l'etat dans lequel l'application s'ouvre,
        // donc le premier que quiconque voit. Les coutures inutilisees ici restent des
        // substituts : ce test regarde le XAML, pas le comportement du scan.
        var erreurs = ErreursEnRealisant(() => new ScanView
        {
            DataContext = new ScanViewModel(
                NSubstitute.Substitute.For<DrDxt.Core.IHardwareDetector>(),
                NSubstitute.Substitute.For<DrDxt.Core.ISettingsStateReader>(),
                NSubstitute.Substitute.For<DrDxt.Core.IBenchmarkRunner>(),
                NSubstitute.Substitute.For<DrDxt.Core.IScanScorer>(),
                NSubstitute.Substitute.For<DrDxt.Core.IStorageDetector>(),
                ScanViewModelTestSeams.Safety(),
                ScanViewModelTestSeams.Engine(),
                ScanViewModelTestSeams.ProfileConsumer(),
                ScanViewModelTestSeams.DesiredStateStore(),
                new AppliedTweakManifestMapper(),
                ScanViewModelTestSeams.BiosAdvisor(),
                ScanViewModelTestSeams.GuideGenerator(),
                cle => cle),
        }, largeur, hauteur);

        erreurs.Should().BeEmpty();
    }

    // ---- L'ecran de scan CHARGE : tout ce que le repos ne montre jamais ----
    //
    // Au repos, les listes sont vides : les DataTemplate des resultats,
    // des composants et des ecrans BIOS ne sont jamais realises, donc jamais cribles.
    // C'est exactement la zone ou l'icone du badge du tableau de bord echouait en
    // silence : une liaison ne casse qu'une fois l'element construit.
    private static ScanViewModel ScanCharge()
    {
        var vm = new ScanViewModel(
            NSubstitute.Substitute.For<DrDxt.Core.IHardwareDetector>(),
            NSubstitute.Substitute.For<DrDxt.Core.ISettingsStateReader>(),
            NSubstitute.Substitute.For<DrDxt.Core.IBenchmarkRunner>(),
            NSubstitute.Substitute.For<DrDxt.Core.IScanScorer>(),
            NSubstitute.Substitute.For<DrDxt.Core.IStorageDetector>(),
            ScanViewModelTestSeams.Safety(),
            ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(),
            ScanViewModelTestSeams.DesiredStateStore(),
            new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(),
            cle => cle);

        vm.State = ScanState.Done;

        // Mesure du 18/09 avant d'ecrire ce test : `Findings` n'est liee a aucun
        // ItemsSource de l'interface (elle sert aux comptes internes). La peupler
        // n'aurait realise aucun gabarit de plus — le test aurait promis un ecran
        // charge en regardant le meme ecran vide. On peuple donc ce que l'ecran
        // AFFICHE vraiment.
        vm.Findings.Add(new FindingViewModel(
            new DrDxt.Core.Finding("cpu", DrDxt.Core.Severity.Major, "finding.cpu", 3.5),
            cle => cle));

        vm.Components.Add(new ComponentItem("Cpu", "Processeur", "i7-14700F"));
        vm.Components.Add(new ComponentItem("Gpu", "Carte graphique", "RTX 4060"));

        vm.VoisinsDuJeu.Add("un.exe");
        vm.VoisinsDuJeu.Add("deux.exe");

        return vm;
    }

    [Theory]
    [InlineData(1240, 900)]
    [InlineData(900, 640)]
    public void L_ecran_de_scan_CHARGE_ne_laisse_aucune_liaison_en_erreur(double largeur, double hauteur)
    {
        var erreurs = ErreursEnRealisant(() => new ScanView { DataContext = ScanCharge() },
                                         largeur, hauteur);

        erreurs.Should().BeEmpty();
    }

    [Fact]
    public void Le_bloc_du_rapport_ne_redit_pas_le_resume_en_liste()
    {
        // Scan réel : sous « 6 point(s) à corriger, dont 1 critique(s) », une liste
        // « Résumé du diagnostic » redisait la même chose en trois lignes vagues (« Problèmes
        // critiques détectés », « Configuration à surveiller », « Quelques optimisations
        // possibles »), alors que l'en-tête l'avait déjà dit. Deux endroits qui disent la même
        // chose finissent par se contredire : c'est arrivé (26097e4). Le bloc garde sa phrase et
        // l'export du rapport.
        var textes = StaApplication.Run(() =>
        {
            var vue = new ScanView { DataContext = ScanCharge() };
            vue.Measure(new Size(1240, 900));
            vue.Arrange(new Rect(0, 0, 1240, 900));
            vue.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            var trouves = new List<string>();
            void Parcourir(DependencyObject d)
            {
                if (d is TextBlock t && !string.IsNullOrEmpty(t.Text)) trouves.Add(t.Text);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    Parcourir(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(vue);
            return trouves;
        });

        textes.Should().Contain("UI.Assistant.ViewReport", "témoin : le bloc du rapport est bien réalisé");
        textes.Should().NotContain("UI.Assistant.SummaryTitle");
    }

    // Le bouton de depart vers le BIOS, lie pour la premiere fois. Ses liaisons passent par
    // BiosGuide : une faute de nom laisserait le bouton invisible sans rien dire.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Le_bouton_de_depart_vers_le_BIOS_ne_laisse_aucune_liaison_en_erreur(bool confirmation)
    {
        var textes = new List<string>();
        var erreurs = ErreursEnRealisant(() =>
        {
            var vm = ScanCharge();
            vm.BiosGuide = new BiosGuideViewModel(ScanViewModelTestSeams.Guide(), cle => cle,
                _ => Task.FromResult(DrDxt.Core.Result.Ok()));
            if (confirmation) vm.BiosGuide.DemanderDepartCommand.Execute(null);
            var vue = new ScanView { DataContext = vm };
            vue.Loaded += (_, _) => { };
            return vue;
        }, 1240, 900);

        erreurs.Should().BeEmpty();

        StaApplication.Run(() =>
        {
            var vm = ScanCharge();
            vm.BiosGuide = new BiosGuideViewModel(ScanViewModelTestSeams.Guide(), cle => cle,
                _ => Task.FromResult(DrDxt.Core.Result.Ok()));
            if (confirmation) vm.BiosGuide.DemanderDepartCommand.Execute(null);
            var vue = new ScanView { DataContext = vm };
            vue.Measure(new Size(1240, 900));
            vue.Arrange(new Rect(0, 0, 1240, 900));
            vue.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            void Parcourir(DependencyObject d)
            {
                if (d is UIElement u && u.Visibility != Visibility.Visible) return;
                if (d is TextBlock t && !string.IsNullOrEmpty(t.Text)) textes.Add(t.Text);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    Parcourir(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(vue);
            return 0;
        });

        // Temoin : ce qu'on affirme sans erreur est bien affiche.
        textes.Should().Contain(confirmation ? "Bios.Depart.Confirmer" : "Bios.Depart.Bouton");
    }

    [Fact]
    public void La_note_du_guide_BIOS_passe_a_la_ligne_au_lieu_d_etre_coupee()
    {
        // Scan réel avec XMP coupé : la note « Étapes les mieux connues : pas encore
        // vérifiées pour ta carte exacte… » était posée dans une pastille, à côté du nom de la carte,
        // dans une pile horizontale. Elle recevait une largeur infinie et sortait de la dalle,
        // coupée net (« tu n'as donc be »).
        const string note = "Étapes les mieux connues : pas encore vérifiées pour ta carte exacte. Le nom exact " +
                            "du réglage à chercher est indiqué, tu n'as donc besoin que de le retrouver dans le menu " +
                            "de ton BIOS, même si son chemin diffère un peu de celui qui est écrit ici.";
        var droite = StaApplication.Run(() =>
        {
            var vm = ScanCharge();
            vm.BiosGuide = new BiosGuideViewModel(
                ScanViewModelTestSeams.Guide(confidence: DrDxt.Core.GuideConfidence.UnverifiedFallback),
                cle => cle == "Scan.Bios.Guide.Unverified" ? note : cle);
            var vue = new ScanView { DataContext = vm };
            vue.Measure(new Size(1240, 900));
            vue.Arrange(new Rect(0, 0, 1240, 900));
            vue.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);

            TextBlock? trouve = null;
            void Parcourir(DependencyObject d)
            {
                if (d is TextBlock t && t.Text == note) trouve = t;
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    Parcourir(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(vue);
            trouve.Should().NotBeNull("témoin : la note du guide est bien réalisée");
            return trouve!.TransformToAncestor(vue).TransformBounds(new Rect(trouve.RenderSize)).Right;
        });

        droite.Should().BeLessThanOrEqualTo(1240, "la note doit tenir dans la fenêtre, pas en sortir coupée");
    }

    [Fact]
    public void Le_crible_charge_realise_bien_les_gabarits_de_resultats()
    {
        // Temoin : si les resultats n'etaient pas realises, le test precedent
        // regarderait le meme ecran vide que celui du repos, en annoncant plus.
        var vm = ScanCharge();
        vm.Components.Should().HaveCount(2);
        vm.VoisinsDuJeu.Should().HaveCount(2);
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public void L_ecoute_verrait_une_liaison_cassee_si_il_y_en_avait_une()
    {
        // Sans ce temoin, un test vert ne prouverait qu'une chose : que personne
        // n'ecoute. On casse volontairement une liaison et on exige de la voir.
        var erreurs = ErreursEnRealisant(() =>
        {
            var bloc = new TextBlock { DataContext = new object() };
            bloc.SetBinding(TextBlock.TextProperty, new Binding("ProprieteQuiNExistePas"));
            return bloc;
        }, 200, 40);

        erreurs.Should().NotBeEmpty("l'ecoute doit capter une liaison vers une propriete absente");
    }
}
