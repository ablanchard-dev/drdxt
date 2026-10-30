using CommunityToolkit.Mvvm.Input;

namespace DrDxt.App.ViewModels;

public partial class ScanViewModel
{
    /// <summary>
    /// Une opération qui mesure la machine ou y écrit est en cours. Une seule à la fois : un scan viderait ce qu'Optimiser
    /// mesure, une remesure du processeur pendant le test de refroidissement enregistrerait une référence faussée, un test
    /// mémoire pendant un palier de sous-tension lui attribuerait ses erreurs, un essai lancé pendant Optimiser toucherait les
    /// leviers que le moteur mesure. Pendant l'une, les boutons des autres se grisent ; les boutons « Arrêter » de chacune
    /// restent à part.
    /// </summary>
    public bool OperationLongueEnCours =>
        State == ScanState.Scanning || CommandesLongues.Any(c => c.IsRunning) || VerificationEnCours
        || RechercheSousTensionEnCours || TestRefroidissementEnCours || TestMemoireEnCours || RemesureCpuEnCours
        || RemesureMemoireEnCours || CadenceEnCours || ReposCarteEnCours || ExpertEnCours || BcdEnCours;

    public bool AucuneOperationLongue => !OperationLongueEnCours;

    /// <summary>L'info-bulle d'un bouton grisé par une opération ; null sans opération, et rien ne s'affiche.</summary>
    public string? InfoOperationEnCours => OperationLongueEnCours ? _localize("UI.OperationEnCours") : null;

    /// <summary>
    /// Annuler un réglage ne s'arrête pas devant une mesure : une mesure perturbée se refait. Il attend une écriture : pendant
    /// Optimiser ou un essai, il retirerait un levier que le moteur mesure ou inscrit au journal ; pendant un retrait
    /// automatique, il rendrait une seconde fois le même instantané.
    /// </summary>
    public bool AucuneEcritureEnCours =>
        !(ApplyCommand.IsRunning || ApplyGpuTuningCommand.IsRunning || EssayerEnAlternanceCommand.IsRunning || ReprendreEssaiCommand.IsRunning
          || RetraitAutomatiqueEnCours);

    /// <summary>L'info-bulle d'« Annuler » grisé ; null quand il est libre.</summary>
    public string? InfoEcritureEnCours => AucuneEcritureEnCours ? null : _localize("UI.OperationEnCours");

    /// <summary>Le scan est fini et a de quoi agir : ce que l'étape suivante et l'info-bulle d'Optimiser jugent, qu'une
    /// opération tourne ou non (le bouton grisé, lui, suit aussi l'opération).</summary>
    private bool ScanPretAAgir => State is ScanState.Done or ScanState.AlreadyOptimal or ScanState.Degraded;

    /// <summary>Les commandes sans drapeau à elles, suivies par leur exécution. Celles qui rendent la machine sûre (rendre
    /// la carte Expert, la remettre à l'usine, annuler le BCD, arrêter l'essai) ne sont jamais grisées : une mesure perturbée
    /// se refait, une carte instable n'attend pas la fin d'un test. Annuler un réglage n'attend qu'une écriture
    /// (<see cref="AucuneEcritureEnCours"/>), et son exécution grise Optimiser le temps de l'invite d'élévation.</summary>
    private IAsyncRelayCommand[] CommandesLongues =>
    [
        ApplyCommand, ApplyGpuTuningCommand, EssayerEnAlternanceCommand, ReprendreEssaiCommand, ArreterEssaiCommand,
        RendreBcdCommand, AnnulerBcdCommand, RemettreLaCarteALUsineCommand, RendreCarteExpertCommand, VerifierPretAJouerCommand,
        TesterLigneCommand, AnnulerLevierCommand, ConfirmerVerificationCommand,
    ];

    private void SuivreLesOperationsLongues()
    {
        foreach (var commande in CommandesLongues)
            commande.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) OperationLongueChangee(); };
    }

    private void OperationLongueChangee()
    {
        OnPropertyChanged(nameof(OperationLongueEnCours));
        OnPropertyChanged(nameof(AucuneOperationLongue));
        OnPropertyChanged(nameof(InfoOperationEnCours));
        OnPropertyChanged(nameof(AucuneEcritureEnCours));
        OnPropertyChanged(nameof(InfoEcritureEnCours));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanApplyGpuTuning));
        OnPropertyChanged(nameof(ExpertPeutPoser));
        OnPropertyChanged(nameof(CanInstallUpdate));
        // Un déclencheur de l'étape tiré pendant l'opération s'est calculé sur un bouton grisé : la fin la réannonce.
        AnnoncerEtape();
        foreach (var commande in new IRelayCommand[]
                 {
                     ScanCommand, DemanderVerificationCommand, LancerRechercheSousTensionCommand, LancerTestRefroidissementCommand,
                     LancerTestMemoireCommand, RemesurerProcesseurCommand, RemesurerMemoireCommand, MesurerCadenceCommand,
                     LancerReparationCommand, RendreLeReposALaCarteCommand, ConfirmerVerificationCommand, InstallUpdateCommand,
                 }.Concat(CommandesLongues))
            commande.NotifyCanExecuteChanged();
    }

    partial void OnStateChanged(ScanState value)
    {
        OperationLongueChangee();
        RedireLeJeuChoisi();
    }
    partial void OnVerificationEnCoursChanged(bool value) => OperationLongueChangee();
    partial void OnRemesureCpuEnCoursChanged(bool value) => OperationLongueChangee();
    partial void OnRemesureMemoireEnCoursChanged(bool value) => OperationLongueChangee();
    partial void OnExpertEnCoursChanged(bool value) => OperationLongueChangee();
    partial void OnBcdEnCoursChanged(bool value) => OperationLongueChangee();
}
