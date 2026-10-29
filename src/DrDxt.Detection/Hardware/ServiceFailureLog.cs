using System.Diagnostics.Eventing.Reader;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Un service qui a echoue, et surtout comment ses echecs sont repartis.</summary>
/// <param name="Service">Le nom du service, tel que le gestionnaire l'enregistre.</param>
/// <param name="EvenementId">L'identifiant du gestionnaire de services.</param>
/// <param name="Occurrences">Combien de fois sur la fenetre demandee.</param>
/// <param name="JoursDistincts">Sur combien de journees differentes. C'est ce champ qui distingue
/// une maladie d'un accident, pas le compte.</param>
/// <param name="PlusAncien">Le premier echec de la fenetre.</param>
/// <param name="PlusRecent">Le dernier.</param>
/// <param name="Nature">Ce que l'identifiant signifie.</param>
/// <param name="Tronque">La lecture s'est arrêtée à son plafond d'événements : les échecs plus anciens n'ont pas été lus, et
/// l'étendue réelle n'est pas connue.</param>
public sealed record ServiceFailure(
    string Service,
    int EvenementId,
    int Occurrences,
    int JoursDistincts,
    DateTimeOffset PlusAncien,
    DateTimeOffset PlusRecent,
    string Nature,
    bool Tronque = false)
{
    /// <summary>L'ecart entre le premier et le dernier echec.</summary>
    public TimeSpan Etendue => PlusRecent - PlusAncien;

    /// <summary>
    /// Une rafale n'est pas une maladie. « 21 echecs sur 30 jours » peut tenir en deux minutes
    /// (releve sur la machine de reference : `atvi-skuld`, `ERROR_ALREADY_EXISTS`, un installeur qui
    /// reessaie). Compter sans regarder la repartition transforme un accident en condition chronique.
    /// </summary>
    /// Une lecture tronquée n'en est jamais une : un service qui plante toutes les minutes depuis trente jours tient en huit
    /// heures dans les 500 événements les plus récents.
    public bool EstUneRafale => Occurrences > 1 && JoursDistincts == 1 && !Tronque;

    /// <summary>Revient-il assez souvent, sur assez de journees, pour meriter un regard ?
    ///
    /// **Non chiffré.** Trois journees distinctes est le plus petit nombre qui exclut « la veille
    /// et aujourd'hui ». Ecrit ici pour etre discute.</summary>
    public bool EstRecurrent => JoursDistincts >= 3;
}

/// <summary>
/// Les services qui echouent : un defaut qui n'a besoin d'aucune reference.
///
/// « Ce service n'a pas pu demarrer » est un fait auto-decrit : aucun `StartMode` de reference n'est necessaire (le
/// meme contournement que le MMCSS). Le lecteur ne diagnostique rien : `atvi-skuld` a echoue 21 fois en 30 jours sur la
/// machine de reference sans effet prouve sur le jeu ; le produit rapporte le fait et son compte.
///
/// Le nom vient de `Properties[0]`, un champ a position fixe quelle que soit la langue, pas du message, localise. Ce n'est
/// pas le nom interne : pour un service Windows c'est le nom d'affichage, traduit lui aussi (« Service antivirus Microsoft
/// Defender », lu le 08/09/2026). Fiable pour compter sur une machine, jamais une cle entre deux machines.
/// </summary>
public interface IServiceFailureLog
{
    /// <summary>Les echecs depuis <paramref name="depuis"/>, les plus frequents d'abord. Une liste
    /// vide est une reponse ; un echec de lecture est un `Fail`, jamais une liste vide.</summary>
    Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis);
}

/// <inheritdoc cref="IServiceFailureLog"/>
public sealed class WindowsServiceFailureLog : IServiceFailureLog
{
    /// <summary>
    /// Les identifiants du gestionnaire de services qui denotent un echec, et ce qu'ils disent.
    ///
    /// On ne retient que ceux dont le sens est un echec sans ambiguite. Un service arrete
    /// proprement, desactive ou demarre a la demande ne produit rien ici, et c'est voulu :
    /// « different » n'est pas « endommage ».
    /// </summary>
    private static readonly IReadOnlySet<int> Natures = new HashSet<int> { 7000, 7001, 7022, 7023, 7024, 7031, 7034 };

    private const int MaxEvenements = 500;

    /// <summary>
    /// Le regroupement, separe de la lecture du journal pour qu'il soit prouvable avec des dates
    /// choisies : des `ServiceFailure` construits a la main eprouvent les proprietes, jamais le calcul
    /// qui les remplit (remplacer les journees distinctes par le compte brut ne tuerait aucun test).
    /// </summary>
    internal static IReadOnlyList<ServiceFailure> Regrouper(
        IEnumerable<(string Service, int Id, DateTimeOffset Quand)> evenements, bool tronque = false) =>
        evenements
            .GroupBy(x => (x.Service, x.Id))
            .Select(g => new ServiceFailure(
                g.Key.Service, g.Key.Id, g.Count(),
                // Le nombre de journees distinctes, pas le compte. C'est lui qui separe une
                // rafale d'installeur d'une panne qui revient.
                g.Select(x => x.Quand.ToLocalTime().Date).Distinct().Count(),
                g.Min(x => x.Quand), g.Max(x => x.Quand),
                Docteur.T(Natures.Contains(g.Key.Id) ? $"SF.{g.Key.Id}" : "SF.Echec"), tronque))
            // On classe par recurrence d'abord : un service qui echoue trois jours de suite compte
            // plus qu'un qui a reessaye vingt fois en deux minutes.
            .OrderByDescending(f => f.JoursDistincts)
            .ThenByDescending(f => f.Occurrences)
            .ThenByDescending(f => f.PlusRecent)
            .ToList();

    public Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis)
    {
        try
        {
            var ids = string.Join(" or ", Natures.Select(i => $"EventID={i}"));
            var requete = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Service Control Manager'] and ({ids})]]")
            {
                ReverseDirection = true,
            };

            var brut = new List<(string Service, int Id, DateTimeOffset Quand)>();
            using var lecteur = new EventLogReader(requete);
            for (var e = lecteur.ReadEvent(); e is not null && brut.Count < MaxEvenements;
                 e = lecteur.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated is not { } t) continue;
                    var quand = new DateTimeOffset(t);
                    if (quand < depuis) continue;

                    // Le nom vit dans les donnees structurees, jamais dans le message traduit.
                    // Absent = on ignore l'evenement plutot que d'inventer un nom.
                    var nom = e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(nom)) continue;

                    brut.Add((nom, e.Id, quand));
                }
            }

            return Result<IReadOnlyList<ServiceFailure>>.Ok(Regrouper(brut, tronque: brut.Count >= MaxEvenements));
        }
        catch (Exception ex)
        {
            // Journal illisible sur une machine durcie : on le dit. Une liste vide ferait passer
            // « je n'ai pas pu lire » pour « aucun service n'echoue ».
            return Result<IReadOnlyList<ServiceFailure>>.Fail(
                ErrorCodes.DetectionFailed, "Journal System illisible : " + ex.Message);
        }
    }
}
