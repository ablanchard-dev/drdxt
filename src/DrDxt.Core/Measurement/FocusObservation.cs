namespace DrDxt.Core;

/// <summary>
/// Qui est au premier plan, maintenant : une lecture, pas une deduction. La couture qui teste le focus sans fenetre et
/// branche le vrai `GetForegroundWindow` en production.
/// </summary>
public interface IForegroundProbe
{
    /// <summary>Le PID du processus dont la fenetre est au premier plan, ou <c>null</c> si la
    /// question n'a pas de reponse fiable (bureau verrouille, transition, lecture refusee) : « je ne sais pas », pas « le
    /// jeu a perdu le focus », qui invaliderait des passes bonnes.</summary>
    int? ForegroundProcessId();
}

/// <summary>
/// Ce que le focus a reellement fait pendant une passe : observe, pas infere de la regularite des frametimes (un jeu qui
/// rame regulierement passerait pour un alt-tab, un alt-tab irregulier pour du jeu). **Une zone hors focus n'est jamais
/// une performance du PC, un ralentissement au premier plan toujours une vraie donnee** : on ne nettoie pas les
/// mauvaises performances du joueur.
/// </summary>
/// <param name="Echantillons">Nombre de lectures effectuees.</param>
/// <param name="AuPremierPlan">Lectures ou le processus cible etait au premier plan.</param>
/// <param name="Inconnues">Lectures sans reponse fiable. Ni pour, ni contre.</param>
public sealed record FocusObservation(int Echantillons, int AuPremierPlan, int Inconnues)
{
    /// <summary>Rien n'a ete observe. Distinct d'une observation qui aurait trouve zero focus.</summary>
    public static readonly FocusObservation Aucune = new(0, 0, 0);

    /// <summary>Part du temps passe au premier plan, parmi les lectures concluantes.
    /// <c>null</c> quand aucune lecture n'a conclu, et « je n'ai pas pu regarder » ne doit jamais
    /// devenir « il n'etait pas la ».</summary>
    public double? FractionAuPremierPlan
    {
        get
        {
            var concluantes = Echantillons - Inconnues;
            return concluantes > 0 ? (double)AuPremierPlan / concluantes : null;
        }
    }

    /// <summary>Part minimale de temps au premier plan pour qu'une passe reste comparable. **Non chiffré** : 90 % laisse
    /// passer une notification d'une seconde sur une minute, et arrete un alt-tab qui dure.</summary>
    public const double FractionMinimale = 0.90;

    /// <summary>La passe a-t-elle ete jouee, ou le joueur etait-il ailleurs ?
    /// <c>true</c> quand rien n'a pu etre observe : Unknown ne condamne pas.</summary>
    public bool PasseComparable => FractionAuPremierPlan is not { } f || f >= FractionMinimale;

    /// <summary>Ajoute une lecture.</summary>
    public FocusObservation Avec(int? auPremierPlan, int pidCible) => auPremierPlan switch
    {
        null => this with { Echantillons = Echantillons + 1, Inconnues = Inconnues + 1 },
        var pid when pid == pidCible => this with
        {
            Echantillons = Echantillons + 1, AuPremierPlan = AuPremierPlan + 1,
        },
        _ => this with { Echantillons = Echantillons + 1 },
    };
}
