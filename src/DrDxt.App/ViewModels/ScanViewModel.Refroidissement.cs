using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

// Le test de refroidissement, en bouton sur la page du processeur : charge pleine sur tous les fils, température lue
// par la sonde, puis verdict. Même gabarit que le test mémoire : maintenir pour lancer, la progression, l'arrêt, le verdict. Le moteur (TestDeRefroidissement) a son arrêt de sécurité ;
// jamais de charge quand un jeu tourne.
public partial class ScanViewModel
{
    /// <summary>Coutures de test : le moteur, la durée, le jeu ouvert, la limite lue. L'écran ne les touche jamais.</summary>
    internal Func<TestDeRefroidissement> FabriqueTestRefroidissement { get; set; } = () => new TestDeRefroidissement(
        TestDeRefroidissement.ChargePleine, DrDxt.Detection.Cpu.TemperaturePawnIo.LireEtat, (d, ct) => Task.Delay(d, ct));
    internal TimeSpan DureeDuTestRefroidissement { get; set; } = TestDeRefroidissement.DureeParDefaut;
    internal Func<bool>? JeuOuvertPourLeTest { get; set; }
    internal Func<LimiteDePuissanceLue?> LimitePourLeTest { get; set; } = DrDxt.Detection.Cpu.TemperaturePawnIo.LireLimites;

    private CancellationTokenSource? _arretTestRefroidissement;

    [ObservableProperty] private bool _testRefroidissementEnCours;
    [ObservableProperty] private double _progressionTestRefroidissement;
    [ObservableProperty] private string _tempsRestantTestRefroidissement = string.Empty;
    [ObservableProperty] private string? _resultatTestRefroidissement;
    [ObservableProperty] private bool _testRefroidissementEnDefaut;

    public bool TestRefroidissementATermine => ResultatTestRefroidissement is not null && !TestRefroidissementEnCours;
    partial void OnResultatTestRefroidissementChanged(string? value) => OnPropertyChanged(nameof(TestRefroidissementATermine));
    partial void OnTestRefroidissementEnCoursChanged(bool value) => OnPropertyChanged(nameof(TestRefroidissementATermine));

    public string LabelTestRefroidissementTitre => _localize("Refroidissement.Test.Titre");
    public string LabelTestRefroidissementDescription => _localize("Refroidissement.Test.Description");
    public string LabelTestRefroidissementMaintenir => _localize("Refroidissement.Test.Maintenir");
    public string LabelTestRefroidissementEnCours => _localize("Refroidissement.Test.EnCours");
    public string LabelTestRefroidissementArreter => _localize("Refroidissement.Test.Arreter");
    public string LabelTestRefroidissementRelancer => _localize("Refroidissement.Test.Relancer");
    public string LabelCapsuleTestRefroidissement => _localize("Agir.Capsule.TestRefroidissement");

    [RelayCommand]
    private async Task LancerTestRefroidissement()
    {
        if (TestRefroidissementEnCours || RechercheSousTensionEnCours) return;
        if ((JeuOuvertPourLeTest ?? _gameLocator.JeuOuvert)())
        {
            TestRefroidissementEnDefaut = false;
            ResultatTestRefroidissement = _localize("Refroidissement.Test.JeuOuvert");
            return;
        }
        _arretTestRefroidissement = new CancellationTokenSource();
        TestRefroidissementEnCours = true;
        ResultatTestRefroidissement = null;
        ProgressionTestRefroidissement = 0;
        var debut = DateTime.UtcNow;
        var duree = DureeDuTestRefroidissement;
        var minuterie = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        minuterie.Tick += (_, _) =>
        {
            var ecoule = DateTime.UtcNow - debut;
            ProgressionTestRefroidissement = Math.Min(1, ecoule / duree);
            var reste = duree - ecoule;
            TempsRestantTestRefroidissement = reste > TimeSpan.Zero
                ? $"{(int)reste.TotalMinutes}:{reste.Seconds:00} {_localize("Memoire.Test.Restant")}"
                : string.Empty;
        };
        minuterie.Start();
        try
        {
            var test = FabriqueTestRefroidissement();
            var jeton = _arretTestRefroidissement.Token;
            var r = await Task.Run(() => test.LancerAsync(duree, jeton), CancellationToken.None);
            var verdict = TestDeRefroidissement.Juger(r, LimitePourLeTest());
            TestRefroidissementEnDefaut = verdict.Statut == RepairStatus.Suspicious;
            ResultatTestRefroidissement = verdict.Explication;
        }
        // Une commande asynchrone qui lève remonte au fil d'interface et peut fermer l'App en plein test : la panne se dit.
        catch (Exception ex)
        {
            TestRefroidissementEnDefaut = false;
            ResultatTestRefroidissement = ex.Message;
        }
        finally
        {
            minuterie.Stop();
            ProgressionTestRefroidissement = 1;
            TempsRestantTestRefroidissement = string.Empty;
            TestRefroidissementEnCours = false;
        }
    }

    [RelayCommand]
    private void ArreterTestRefroidissement() => _arretTestRefroidissement?.Cancel();
}
