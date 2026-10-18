namespace DrDxt.Core;

/// <summary>Ce que Windows dit de sa mémoire à l'instant du relevé (GlobalMemoryStatusEx). L'engagement est ce que les
/// programmes ont réservé (mémoire + fichier d'échange) ; sa limite est la somme des deux.</summary>
public sealed record MemoireLue(ulong TotalOctets, ulong DisponiblesOctets, ulong EngagementOctets, ulong LimiteEngagementOctets);

/// <summary>
/// Un PC de bureau sans jeu gagne aussi en fluidité : une mémoire pleine fait déborder sur le disque ce qui ne tient
/// plus. Fenêtres qui mettent une seconde à revenir, souris qui accroche, saccades : c'est le défaut de fluidité le plus
/// courant d'un PC de bureau, et il n'a rien à voir avec le jeu. Lecture seule : DrDXT ne ferme rien, ne touche pas au
/// fichier d'échange ; il dit, et donne les gestes. Un relevé est un instant : il le dit aussi.
///
/// Deux choses distinctes, deux constats. La mémoire libre basse veut dire que Windows pagine déjà ;
/// l'engagement près de sa limite veut dire que la mémoire virtuelle (mémoire + fichier d'échange) est presque réservée en
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

    private static string Gestes => Docteur.T("MS.Gestes");

    /// <summary>Seuils de pagination, en lectures de pages dures par seconde. **Choix DrDXT, pas une norme Microsoft** :
    /// sous 50, le disque sert surtout des fichiers ouverts et des chargements ; au-dessus de 500, une lecture sur disque
    /// attend presque à chaque instant. Mesuré le 16/10/2026 sur la machine de référence au repos : 0 par seconde.</summary>
    public const int PaginationSeuil = 50, PaginationForte = 500;

    /// <param name="lecturesDures">Lectures de pages dures par seconde sur deux secondes ; <c>null</c> = non mesuré.</param>
    public static RepairFinding Juger(MemoireLue? lu, double? lecturesDures = null)
    {
        if (lu is null || lu.TotalOctets == 0)
            return new RepairFinding(Domaine, Nom, null, null, RepairStatus.Unknown,
                Docteur.T("MS.NonLue"));

        var librePct = (int)(lu.DisponiblesOctets * 100 / lu.TotalOctets);
        var engagementPct = lu.LimiteEngagementOctets == 0 ? 0 : (int)(lu.EngagementOctets * 100 / lu.LimiteEngagementOctets);
        var courante = Docteur.T("MS.Courante", Go(lu.DisponiblesOctets), Go(lu.TotalOctets), librePct, engagementPct);
        var attendue = Docteur.T("MS.Attendue", LibreMinimumPourcent, EngagementMaximumPourcent);

        var pagination = lecturesDures is not { } l ? null
            : l >= PaginationForte ? "MS.PagineFort" : l >= PaginationSeuil ? "MS.Pagine" : "MS.Calme";
        if (lecturesDures is { } lp)
            courante += Docteur.T("MS.Lectures", Math.Round(lp).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var mesure = pagination is null ? "" : Docteur.T(pagination) + Docteur.T("MS.CompteurInclut");

        // Pleine mais calme : la mémoire est prise, rien n'attend le disque ; c'est dit, sans alarme ni « purge ».
        if (librePct < LibreMinimumPourcent && pagination == "MS.Calme")
            return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.Observed, Docteur.T("MS.PleineCalme") + mesure);

        if (librePct < LibreMinimumPourcent || pagination == "MS.PagineFort")
            return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.Suspicious,
                Docteur.T(librePct < LibreMinimumPourcent ? "MS.Pleine" : "MS.PagineSansEtrePleine") + mesure + Gestes,
                RepairAction.Recommandation);

        if (engagementPct > EngagementMaximumPourcent)
            return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.Suspicious,
                Docteur.T("MS.Virtuelle") + Gestes,
                RepairAction.Recommandation);

        // De la place, et Windows lit un peu le disque : ce n'est pas « rien », ce n'est pas une alarme.
        if (pagination == "MS.Pagine")
            return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.Observed, Docteur.T("MS.PagineUnPeu") + mesure);

        return new RepairFinding(Domaine, Nom, courante, attendue, RepairStatus.ExpectedDefault,
            Docteur.T("MS.Rien") + mesure);
    }

    private static string Go(ulong octets) => (octets / 1073741824.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
