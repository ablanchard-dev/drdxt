namespace DrDxt.Core;

/// <summary>Ce que Windows dit de sa mémoire à l'instant du relevé (GlobalMemoryStatusEx). L'engagement est ce que les
/// programmes ont réservé (mémoire + fichier d'échange) ; sa limite est la somme des deux.</summary>
public sealed record MemoireLue(ulong TotalOctets, ulong DisponiblesOctets, ulong EngagementOctets, ulong LimiteEngagementOctets);

/// <summary>
/// BUREAU (09/10/2026, le mainteneur : « un PC de bureau sans jeu, capable quand même de gains de latence et de fluidité ;
/// si les RAM sont saturées, trouver des possibilités ») : une mémoire pleine fait déborder sur le disque ce qui ne tient
/// plus. Fenêtres qui mettent une seconde à revenir, souris qui accroche, saccades : c'est le défaut de fluidité le plus
/// courant d'un PC de bureau, et il n'a rien à voir avec le jeu. Lecture seule : DrDXT ne ferme rien, ne touche pas au
/// fichier d'échange ; il dit, et donne les gestes. Un relevé est un instant : il le dit aussi.
///
/// Revue du 09/10/2026 : deux choses distinctes, deux constats. La mémoire LIBRE basse veut dire que Windows pagine déjà ;
/// l'ENGAGEMENT près de sa limite veut dire que la mémoire virtuelle (mémoire + fichier d'échange) est presque réservée en
/// entier : le prochain programme peut manquer de mémoire, sans que rien ne pagine encore. Les seuils (10 % libres, 90 % de
/// la limite) sont des seuils DrDXT, pas une norme Microsoft.
/// Source : Microsoft Learn, MEMORYSTATUSEX (ullAvailPhys, ullTotalPageFile = limite d'engagement courante).
/// </summary>
public static class MemoireSaturee
{
    public const string Domaine = "MEMOIRE";
    public const string Nom = "mémoire saturée";

    public const int LibreMinimumPourcent = 10;
    public const int EngagementMaximumPourcent = 90;

    private const string Gestes = " Les gestes, du plus simple au plus durable : ferme ce qui ne sert pas (les onglets de navigateur "
        + "d'abord, ils comptent pour beaucoup) ; retire du démarrage les programmes que DrDXT nomme plus bas ; laisse le fichier "
        + "d'échange « géré par le système » sur un SSD ; si c'est tous les jours, une barrette de plus est le seul vrai remède. "
        + "DrDXT ne ferme rien et ne touche pas au fichier d'échange.";

    public static RepairFinding Juger(MemoireLue? lu)
    {
        if (lu is null || lu.TotalOctets == 0)
            return new RepairFinding(Domaine, Nom, null, null, RepairStatus.Unknown,
                "la pression mémoire n'a pas pu être lue.");

        var librePct = (int)(lu.DisponiblesOctets * 100 / lu.TotalOctets);
        var engagementPct = lu.LimiteEngagementOctets == 0 ? 0 : (int)(lu.EngagementOctets * 100 / lu.LimiteEngagementOctets);
        var courante = $"{Go(lu.DisponiblesOctets)} Go libres sur {Go(lu.TotalOctets)} ({librePct} %), engagement {engagementPct} % de la limite";
        var attendue = $"au moins {LibreMinimumPourcent} % libres, engagement sous {EngagementMaximumPourcent} % (seuils DrDXT)";

        if (librePct < LibreMinimumPourcent)
            return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.Suspicious,
                "la mémoire est pleine à l'instant du relevé : ce qui ne tient plus part sur le disque, et tout ce qui est "
                + "ouvert ralentit (fenêtres qui reviennent avec retard, souris qui accroche, saccades)." + Gestes,
                RepairAction.Recommandation);

        if (engagementPct > EngagementMaximumPourcent)
            return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.Suspicious,
                "la mémoire virtuelle (mémoire + fichier d'échange) est presque entièrement réservée à l'instant du relevé : rien "
                + "ne pagine forcément encore, mais le prochain programme ouvert peut manquer de mémoire ou la faire déborder."
                + Gestes,
                RepairAction.Recommandation);

        return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.ExpectedDefault,
            "pas de pression visible à l'instant du relevé.");
    }

    private static string Go(ulong octets) => (octets / 1073741824.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
