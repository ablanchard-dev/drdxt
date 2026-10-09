using System.Diagnostics.Eventing.Reader;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Un service qui a echoue, et — surtout — comment ses echecs sont REPARTIS.</summary>
/// <param name="Service">Le nom du service, tel que le gestionnaire l'enregistre.</param>
/// <param name="EvenementId">L'identifiant du gestionnaire de services.</param>
/// <param name="Occurrences">Combien de fois sur la fenetre demandee.</param>
/// <param name="JoursDistincts">Sur combien de journees differentes. C'est ce champ qui distingue
/// une maladie d'un accident, pas le compte.</param>
/// <param name="PlusAncien">Le premier echec de la fenetre.</param>
/// <param name="PlusRecent">Le dernier.</param>
/// <param name="Nature">Ce que l'identifiant signifie.</param>
public sealed record ServiceFailure(
    string Service,
    int EvenementId,
    int Occurrences,
    int JoursDistincts,
    DateTimeOffset PlusAncien,
    DateTimeOffset PlusRecent,
    string Nature)
{
    /// <summary>L'ecart entre le premier et le dernier echec.</summary>
    public TimeSpan Etendue => PlusRecent - PlusAncien;

    /// <summary>
    /// Une rafale n'est pas une maladie — et c'est la correction du 08/09/2026.
    ///
    /// La premiere version de ce lecteur rendait « 21 echecs sur 30 jours » pour `atvi-skuld` et
    /// le classait `Suspicious` a partir de cinq occurrences. Le compte etait juste et la lecture
    /// fausse : **les 21 echecs tiennent entre 18:14 et 18:16 le 01/09**, une seule rafale de deux
    /// minutes, avec `ERROR_ALREADY_EXISTS` — un installeur qui reessaie. Le service n'existe meme
    /// plus sur la machine.
    ///
    /// Compter sans regarder la REPARTITION transforme un accident en condition chronique. C'est
    /// une facon nouvelle de mentir avec un chiffre exact.
    /// </summary>
    public bool EstUneRafale => Occurrences > 1 && JoursDistincts == 1;

    /// <summary>Revient-il assez souvent, sur assez de journees, pour meriter un regard ?
    ///
    /// **NON CHIFFRÉ.** Trois journees distinctes est le plus petit nombre qui exclut « la veille
    /// et aujourd'hui ». Ecrit ici pour etre discute.</summary>
    public bool EstRecurrent => JoursDistincts >= 3;
}

/// <summary>
/// Les services qui echouent — un defaut qui n'a besoin d'aucune reference.
///
/// Le probleme de tout audit de reparation est qu'il faut connaitre le defaut pour dire qu'une
/// valeur en devie. Pour les services, la question ne se pose pas : « ce service n'a pas pu
/// demarrer » est un fait AUTO-DECRIT. Aucun `StartMode` de reference n'est necessaire, aucune
/// opinion sur ce qui devrait tourner. Le systeme dit lui-meme que quelque chose a rate.
///
/// C'est le meme contournement que pour le MMCSS : quand le defaut est hors de portee, chercher
/// l'affirmation qui ne le demande pas.
///
/// Et ce lecteur ne diagnostique rien. Un service qui echoue n'est pas forcement une cause de
/// perte de performance, et souvent pas une cause du tout — `atvi-skuld` a echoue 21 fois en 30
/// jours sur la machine de reference sans que rien ne prouve un effet sur le jeu. Le produit
/// rapporte le fait et son compte ; l'attribution reste a faire.
///
/// ══ pourquoi le nom du service ne vient pas du message ══
/// Le message entier est LOCALISE : sur cette machine il dit « Le service Steam Client Service
/// n'a pas pu demarrer en raison de l'erreur : ... ». Une expression reguliere calee sur « The
/// service X failed to start » ne trouverait rien ici, et une calee sur le francais ne trouverait
/// rien ailleurs. Le nom est donc lu dans les DONNEES structurees (`Properties[0]`), un champ a
/// position fixe quelle que soit la langue.
///
/// Ce que ce champ n'est pas (une premiere version le disait a tort) : il ne contient pas le nom
/// INTERNE du service. Pour les services Windows il porte le nom d'affichage, lui aussi traduit —
/// mesure le 08/09/2026 : « Service antivirus Microsoft Defender », « SMP de l'Espace de stockages
/// Microsoft ». Ce qui est acquis reste vrai et suffit ici : la POSITION du champ ne depend pas de
/// la langue, donc le regroupement et le comptage sont fiables sur une machine donnee. Mais ce nom
/// ne doit jamais servir de CLE pour comparer deux machines ou pour interroger le gestionnaire de
/// services.
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
    /// proprement, desactive ou demarre a la demande ne produit rien ici — et c'est voulu :
    /// « different » n'est pas « endommage ».
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> Natures = new Dictionary<int, string>
    {
        [7000] = "n'a pas pu démarrer",
        [7001] = "dépend d'un service qui n'a pas pu démarrer",
        [7022] = "s'est bloqué au démarrage",
        [7023] = "s'est terminé avec une erreur",
        [7024] = "s'est terminé avec une erreur propre au service",
        [7031] = "s'est terminé de façon inattendue",
        [7034] = "s'est terminé de façon inattendue, sans action de récupération",
    };

    private const int MaxEvenements = 500;

    /// <summary>
    /// Le regroupement, SEPARE de la lecture du journal pour qu'il soit prouvable.
    ///
    /// Il ne l'etait pas. Une mutation du 08/09/2026 — remplacer le compte de journees
    /// distinctes par le compte brut d'occurrences — n'a tue aucun test : les tests construisaient
    /// des `ServiceFailure` a la main et eprouvaient les proprietes, jamais le calcul qui les
    /// remplit. Une garantie nommee sans etre prouvee, la seconde de la journee.
    ///
    /// L'extraire coute une methode et rend le seul calcul qui porte le sens verifiable avec des
    /// dates choisies.
    /// </summary>
    internal static IReadOnlyList<ServiceFailure> Regrouper(
        IEnumerable<(string Service, int Id, DateTimeOffset Quand)> evenements) =>
        evenements
            .GroupBy(x => (x.Service, x.Id))
            .Select(g => new ServiceFailure(
                g.Key.Service, g.Key.Id, g.Count(),
                // Le nombre de JOURNEES distinctes, pas le compte. C'est lui qui separe une
                // rafale d'installeur d'une panne qui revient.
                g.Select(x => x.Quand.ToLocalTime().Date).Distinct().Count(),
                g.Min(x => x.Quand), g.Max(x => x.Quand),
                Natures.GetValueOrDefault(g.Key.Id, "echec")))
            // On classe par RECURRENCE d'abord : un service qui echoue trois jours de suite compte
            // plus qu'un qui a reessaye vingt fois en deux minutes.
            .OrderByDescending(f => f.JoursDistincts)
            .ThenByDescending(f => f.Occurrences)
            .ThenByDescending(f => f.PlusRecent)
            .ToList();

    public Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis)
    {
        try
        {
            var ids = string.Join(" or ", Natures.Keys.Select(i => $"EventID={i}"));
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

            return Result<IReadOnlyList<ServiceFailure>>.Ok(Regrouper(brut));
        }
        catch (Exception ex)
        {
            // Journal illisible sur une machine durcie : on le DIT. Une liste vide ferait passer
            // « je n'ai pas pu lire » pour « aucun service n'echoue ».
            return Result<IReadOnlyList<ServiceFailure>>.Fail(
                ErrorCodes.DetectionFailed, "Journal System illisible : " + ex.Message);
        }
    }
}
