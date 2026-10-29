using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>La carte Agir : la cadence des entrées et le test mémoire en session.</summary>
public sealed partial class ScanViewModel
{
    // Le dernier test memoire en session, rendu dans la sante au prochain audit.
    private RepairFinding? _constatTestMemoire;

    // ── La carte « Agir » du domaine latence : ce que la souris et la manette envoient vraiment ──────────
    /// <summary>La mesure des entrées (cinq secondes d'écoute, puis le constat) ; l'appli la pose, null = carte cachée.</summary>
    internal Func<CancellationToken, RepairFinding>? MesureDesEntrees { get; set; }
    public bool MesureDesEntreesDisponible => MesureDesEntrees is not null;
    private RepairFinding? _constatCadence;
    [ObservableProperty] private bool _cadenceEnCours;
    [ObservableProperty] private string? _resultatCadence;
    public bool CadencePrete => !CadenceEnCours;
    partial void OnCadenceEnCoursChanged(bool value) { OnPropertyChanged(nameof(CadencePrete)); OperationLongueChangee(); }
    public string LabelCapsuleCadence => _localize("Agir.Capsule.Cadence");
    public string LabelCadenceTitre => _localize(SansManette ? "Entrees.Cadence.Titre.SansManette" : "Entrees.Cadence.Titre");
    public string LabelCadenceDescription => _localize(SansManette ? "Entrees.Cadence.Description.SansManette" : "Entrees.Cadence.Description");
    public string LabelCadenceMaintenir => _localize(SansManette ? "Entrees.Cadence.Maintenir.SansManette" : "Entrees.Cadence.Maintenir");
    public string LabelCadenceEnCours => _localize(SansManette ? "Entrees.Cadence.EnCours.SansManette" : "Entrees.Cadence.EnCours");

    /// <summary>Une manette est-elle branchée ? Posé au lancement ; null = non lu, le texte nomme alors la manette.</summary>
    internal Func<bool>? ManetteBranchee { get; set; }

    // « bouge la souris et la manette » ne se dit qu'à qui en a une, ou quand on ne sait pas.
    private bool SansManette => ManetteBranchee?.Invoke() == false;

    /// <summary>Le maintien du noyau est la confirmation ; l'écoute tourne hors du fil d'interface.</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task MesurerCadence()
    {
        if (CadenceEnCours || MesureDesEntrees is not { } mesure) return;
        CadenceEnCours = true;
        ResultatCadence = null;
        try
        {
            _constatCadence = await Task.Run(() => mesure(CancellationToken.None));
            ResultatCadence = _constatCadence.Explication;
        }
        catch (Exception ex) { ResultatCadence = RaisonLisible.Dire(ex, _localize); }
        finally { CadenceEnCours = false; }
    }

    /// <summary>Le point d'étape du test mémoire, dans les données de DrDXT ; null = aucun (les tests qui ne le vérifient
    /// pas).</summary>
    internal string? FichierRepriseTestMemoire { get; set; }

    /// <summary>Le jugement d'un test resté sans fin (l'appli lit le point d'étape et les journaux) ; null = rien lu.</summary>
    internal Func<RepairFinding?>? JugerRepriseTestMemoire { get; set; }

    // ── La carte « Agir » du domaine memoire ──────────────────
    public static readonly TimeSpan DureeTestMemoire = TimeSpan.FromMinutes(5);
    // Coutures de test : cinq minutes et un quart de la memoire ne se testent pas en suite. L'ecran
    // ne les touche jamais ; null = la valeur du produit.
    internal TimeSpan DureeDuTestMemoire { get; set; } = DureeTestMemoire;
    internal long? VolumeDuTestMemoire { get; set; }
    private CancellationTokenSource? _arretTestMemoire;

    [ObservableProperty] private bool _testMemoireEnCours;
    [ObservableProperty] private double _progressionTestMemoire;
    [ObservableProperty] private string _tempsRestantTestMemoire = string.Empty;
    [ObservableProperty] private string? _resultatTestMemoire;
    [ObservableProperty] private bool _testMemoireEnDefaut;

    public bool TestMemoireATermine => ResultatTestMemoire is not null && !TestMemoireEnCours;
    partial void OnResultatTestMemoireChanged(string? value) => OnPropertyChanged(nameof(TestMemoireATermine));
    partial void OnTestMemoireEnCoursChanged(bool value) { OnPropertyChanged(nameof(TestMemoireATermine)); OperationLongueChangee(); }

    public string LabelCarteAgir => _localize("Carte.Agir");
    public string LabelTestMemoireTitre => _localize("Memoire.Test.Titre");
    public string LabelTestMemoireDescription => _localize("Memoire.Test.Description");
    public string LabelTestMemoireMaintenir => _localize("Memoire.Test.Maintenir");
    public string LabelTestMemoireEnCours => _localize("Memoire.Test.EnCours");
    public string LabelTestMemoireArreter => _localize("Memoire.Test.Arreter");
    public string LabelTestMemoireRelancer => _localize("Memoire.Test.Relancer");

    /// <summary>Lance le test. Le maintien du noyau est la confirmation ; l'interface n'est jamais
    /// figee (le test tourne hors du fil d'interface) et l'arret se prend entre deux passes.</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task LancerTestMemoire()
    {
        if (TestMemoireEnCours) return;
        _arretTestMemoire = new CancellationTokenSource();
        TestMemoireEnCours = true;
        ResultatTestMemoire = null;
        ProgressionTestMemoire = 0;
        var debut = DateTime.UtcNow;
        var minuterie = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        minuterie.Tick += (_, _) =>
        {
            var ecoule = DateTime.UtcNow - debut;
            ProgressionTestMemoire = Math.Min(1, ecoule / DureeDuTestMemoire);
            var reste = DureeDuTestMemoire - ecoule;
            TempsRestantTestMemoire = reste > TimeSpan.Zero
                ? $"{(int)reste.TotalMinutes}:{reste.Seconds:00} {_localize("Memoire.Test.Restant")}"
                : string.Empty;
        };
        minuterie.Start();
        try
        {
            var r = await TesterMemoireEnSessionAsync(DureeDuTestMemoire, VolumeDuTestMemoire, _arretTestMemoire.Token);
            TestMemoireEnDefaut = r.Erreurs > 0;
            ResultatTestMemoire = r.Phrase;
        }
        catch (Exception ex)
        {
            TestMemoireEnDefaut = false;
            ResultatTestMemoire = RaisonLisible.Dire(ex, _localize);
        }
        finally
        {
            minuterie.Stop();
            ProgressionTestMemoire = 1;
            TempsRestantTestMemoire = string.Empty;
            TestMemoireEnCours = false;
        }
    }

    [RelayCommand]
    private void ArreterTestMemoire() => _arretTestMemoire?.Cancel();

    /// <summary>Teste la RAM sans redemarrer (motifs ecrits puis relus sur tous les coeurs). Le volume
    /// par defaut est le quart de la memoire, borne a la moitie de la memoire libre : assez pour charger le
    /// controleur, pas assez pour faire paginer la machine. Zero erreur n'est jamais « sain » : c'est « observe ».</summary>
    public async Task<DrDxt.Measurement.Memory.ResultatTestMemoire> TesterMemoireEnSessionAsync(
        TimeSpan duree, long? octets = null, CancellationToken ct = default)
    {
        var volume = octets ?? DrDxt.Measurement.Memory.TestMemoireEnSession.Volume(
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, DrDxt.Measurement.CommitPressureReader.LibrePhysique());
        // Le point d'étape : posé avant, relevé à chaque passe, effacé à la fin normale (arrêt du joueur compris). Resté
        // en place, il dit au lancement suivant que la machine s'est arrêtée pendant le test.
        var debut = DateTimeOffset.Now;
        void Point(int passes)
        {
            if (FichierRepriseTestMemoire is { } f)
                DrDxt.Measurement.Memory.RepriseDuTestMemoire.Poser(f, new(debut, DateTimeOffset.Now, passes));
        }
        Point(0);
        var r = await Task.Run(() => DrDxt.Measurement.Memory.TestMemoireEnSession.Lancer(
            volume, duree, Environment.ProcessorCount, ct, Point));
        if (FichierRepriseTestMemoire is { } fini) DrDxt.Measurement.Memory.RepriseDuTestMemoire.Effacer(fini);
        _constatTestMemoire = DrDxt.Measurement.Memory.TestMemoireEnSession.Constat(r);
        return r;
    }

    // ── La carte « Agir » du domaine reseau ─────────────────────────────────────────────────
    [ObservableProperty] private string? _erreurPlafondEnvoi;
    [ObservableProperty] private bool _plafondEnvoiEnChangement;
}
