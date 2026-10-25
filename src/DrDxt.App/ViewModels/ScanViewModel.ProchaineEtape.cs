using System.Globalization;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Après le scan, le docteur dit
/// en une phrase ce qui vient ensuite, avec un seul bouton, avant la liste des constats. L'ordre (régler d'abord,
/// optimiser ensuite) : un essai qui tourne, sinon le premier point à faire soi-même (et « puis
/// Optimiser » s'il a du travail), sinon ce que DrDXT fait seul (Optimiser), sinon « joue ». Optimiser reste cliquable
/// dans tous les cas : il ne dit jamais non, il fait sa part.
/// </summary>
public partial class ScanViewModel
{
    private enum EtapeDuDocteur { Aucune, Optimiser, EssaiEnCours, AFaire, AFairePuisOptimiser, Jouer }

    private bool OptimiserADuTravail => ReposCarteAOuvrir || !RienAPoserSeul || EssaiProposable;

    private EtapeDuDocteur EtapeSuivante =>
        !ScanPretAAgir ? EtapeDuDocteur.Aucune
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

    /// <summary>L'étape quand la liste ne la dit pas déjà : un point à faire soi-même est le premier de la liste,
    /// cliquable vers son domaine, et ne se redit pas dans un encadré au-dessus. Quand Optimiser a aussi du travail, la
    /// seconde moitié de la consigne (« puis Optimise ») reste dite.</summary>
    public string? EtapeHorsListe => EtapeSuivante switch
    {
        EtapeDuDocteur.AFaire => null,
        // Optimiser est juste sous le verdict, la liste repliée dit le point à la main : rien à redire entre les deux.
        EtapeDuDocteur.AFairePuisOptimiser => null,
        _ => ProchaineEtape,
    };

    private static readonly HashSet<string> CeQuiChangeLEtape = new(StringComparer.Ordinal)
    {
        nameof(State), nameof(RienAPoserSeul), nameof(EssaiEnCours), nameof(EssaiProposable), nameof(ReposCarteAOuvrir),
        nameof(ReposCarteEnAttente),
    };

    // Un seul abonnement plutôt qu'une notification dans chaque setter qui touche l'étape. La liste « à faire » se
    // reconstruit d'un bloc (AnnoncerSatellites) : une annonce par reconstruction, pas une par ligne (
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
        OnPropertyChanged(nameof(EtapeHorsListe));
        OnPropertyChanged(nameof(RienAPoserDuTout));
        OnPropertyChanged(nameof(ApplyHintKey));
        OnPropertyChanged(nameof(ApplyHint));
    }
}
