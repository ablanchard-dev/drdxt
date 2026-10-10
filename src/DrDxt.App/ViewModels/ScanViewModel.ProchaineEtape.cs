using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace DrDxt.App.ViewModels;

/// <summary>
/// 10/10/2026 (le mainteneur : « le côté Dr n'y est pas », « faut que ça sorte de lui ») : après le scan, le docteur dit
/// en une phrase ce qui vient ensuite, avec un seul bouton, avant la liste des constats. L'ordre, revu le soir même
/// (« faut régler et ensuite pouvoir opti ») : un essai qui tourne, sinon le premier point à faire soi-même (et « puis
/// Optimiser » s'il a du travail), sinon ce que DrDXT fait seul (Optimiser), sinon « joue ». Optimiser reste cliquable
/// dans tous les cas : il ne dit jamais non, il fait sa part.
/// </summary>
public partial class ScanViewModel
{
    private enum EtapeDuDocteur { Aucune, Optimiser, EssaiEnCours, AFaire, AFairePuisOptimiser, Jouer }

    private bool OptimiserADuTravail => ReposCarteAOuvrir || !RienAPoserSeul || EssaiProposable;

    private EtapeDuDocteur EtapeSuivante =>
        !CanApply ? EtapeDuDocteur.Aucune
        : EssaiEnCours ? EtapeDuDocteur.EssaiEnCours
        : PointsALaMain.Any() ? (OptimiserADuTravail ? EtapeDuDocteur.AFairePuisOptimiser : EtapeDuDocteur.AFaire)
        : OptimiserADuTravail ? EtapeDuDocteur.Optimiser
        : EtapeDuDocteur.Jouer;

    private PointAFaire PremierPoint => PointsALaMain.First();

    private bool EtapeALaMain => EtapeSuivante is EtapeDuDocteur.AFaire or EtapeDuDocteur.AFairePuisOptimiser;

    public string? ProchaineEtape => EtapeSuivante switch
    {
        EtapeDuDocteur.Optimiser => _localize("Etape.Optimiser"),
        EtapeDuDocteur.EssaiEnCours => _localize("Etape.EssaiEnCours"),
        EtapeDuDocteur.AFaire => string.Format(CultureInfo.CurrentCulture, _localize("Etape.AFaire"),
            PremierPoint.Entree.Titre.TrimEnd('.', ' '), PremierPoint.Domaine.Nom),
        EtapeDuDocteur.AFairePuisOptimiser => string.Format(CultureInfo.CurrentCulture, _localize("Etape.AFairePuisOptimiser"),
            PremierPoint.Entree.Titre.TrimEnd('.', ' '), PremierPoint.Domaine.Nom),
        EtapeDuDocteur.Jouer => _localize("Etape.Jouer"),
        _ => null,
    };

    /// <summary>Le libellé du bouton de l'étape ; <c>null</c> quand il n'y a rien à cliquer. Pas de second
    /// « Optimiser » : le vrai est juste en dessous (un seul appel à l'action, critique du 29/09).</summary>
    public string? ProchaineEtapeBouton => EtapeALaMain ? _localize("Etape.AFaire.Bouton") : null;

    [RelayCommand]
    private void SuivreLaProchaineEtape()
    {
        if (EtapeALaMain) ChoisirDomaine(PremierPoint.Domaine);
    }

    private static readonly HashSet<string> CeQuiChangeLEtape = new(StringComparer.Ordinal)
    {
        nameof(State), nameof(RienAPoserSeul), nameof(EssaiEnCours), nameof(EssaiProposable), nameof(ReposCarteAOuvrir),
        nameof(ReposCarteEnAttente),
    };

    // Un seul abonnement plutôt qu'une notification dans chaque setter qui touche l'étape. La liste « à faire » se
    // reconstruit d'un bloc (AnnoncerSatellites) : une annonce par reconstruction, pas une par ligne (revue du 10/10/2026 :
    // chaque évaluation de l'étape relit l'essai sur disque).
    private void AbonnerLaProchaineEtape()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } nom && CeQuiChangeLEtape.Contains(nom)) AnnoncerEtape();
        };
    }

    /// <summary>« Rien à poser par Optimiser » n'est vrai que si Optimiser n'a pas non plus de geste ni d'essai à lancer.</summary>
    public bool RienAPoserDuTout => RienAPoserSeul && !OptimiserADuTravail;

    private void AnnoncerEtape()
    {
        OnPropertyChanged(nameof(ProchaineEtape));
        OnPropertyChanged(nameof(ProchaineEtapeBouton));
        OnPropertyChanged(nameof(RienAPoserDuTout));
        OnPropertyChanged(nameof(ApplyHintKey));
        OnPropertyChanged(nameof(ApplyHint));
    }
}
