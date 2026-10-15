namespace DrDxt.Core;

/// <summary>
/// Qui est au premier plan, maintenant. Une lecture, pas une deduction.
///
/// C'est la couture qui permet de tester toute la logique de focus sans fenetre, et de brancher
/// le vrai `GetForegroundWindow` en production.
/// </summary>
public interface IForegroundProbe
{
    /// <summary>Le PID du processus dont la fenetre est au premier plan, ou <c>null</c> si la
    /// question n'a pas de reponse fiable (bureau verrouille, transition, lecture refusee).
    ///
    /// <c>null</c> n'est pas « le jeu a perdu le focus » : c'est « je ne sais pas ». Les deux se
    /// traitent differemment, et les confondre ferait invalider des passes parfaitement bonnes.</summary>
    int? ForegroundProcessId();
}

/// <summary>
/// Ce que le focus a reellement fait pendant une passe : observe, pas infere.
///
/// Jusqu'ici, DrDXT devinait l'alt-tab a partir de la regularite des frametimes : une seconde
/// lente et reguliere ressemble a un plafond hors focus. C'est une inference, et elle se trompe
/// dans les deux sens : un jeu qui rame regulierement passe pour un alt-tab, un alt-tab irregulier
/// passe pour du jeu.
///
/// Ici on regarde. La difference tient en une phrase : **une zone hors focus n'est jamais une
/// performance du PC, et un ralentissement avec le jeu au premier plan est toujours une vraie
/// donnee.** Le produit mesure l'experience vecue ; il ne nettoie pas les mauvaises performances
/// du joueur.
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

    /// <summary>Part minimale de temps au premier plan pour qu'une passe reste comparable.
    ///
    /// **Non chiffré.** 90 % laisse passer une notification ou un changement de fenetre d'une
    /// seconde sur une passe d'une minute, et arrete un alt-tab qui dure. Ecrit ici pour etre
    /// discute, pas dilue dans une condition.</summary>
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
