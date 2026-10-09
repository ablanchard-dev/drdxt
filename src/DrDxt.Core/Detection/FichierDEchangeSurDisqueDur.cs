namespace DrDxt.Core;

/// <summary>
/// 08/10/2026 (chantier D5, suite de <see cref="JeuxSurDisqueDur"/>) : un fichier d'échange sur un disque dur alors qu'un
/// SSD est là ralentit tout ce qui déborde de la mémoire (chargements, retours au bureau). Lecture seule : DrDXT ne le
/// déplace pas ; le geste est dans Windows. Le type vient de MSFT_PhysicalDisk.MediaType (3 = disque dur, 4 = SSD,
/// 0 = non dit) ; un type non dit n'est jamais jugé.
/// </summary>
public static class FichierDEchangeSurDisqueDur
{
    public const string Nom = "emplacement";

    /// <param name="fichiers">Chaque fichier d'échange (chemin, type du disque qui le porte) ; null = non lu.</param>
    /// <param name="ssdPresent">Un SSD interne est-il présent sur ce PC ? <c>null</c> : un disque n'a pas dit son type, rien
    /// n'est affirmé (revue du 08/10/2026 : « aucun SSD vu » se disait sur une lecture incomplète). Un SSD branché en USB ne
    /// compte pas : on n'y conseille pas le fichier d'échange.</param>
    public static RepairFinding Juger(IReadOnlyList<(string Chemin, int TypeDeDisque)>? fichiers, bool? ssdPresent)
    {
        if (fichiers is null)
            return new RepairFinding("PAGEFILE", Nom, null, null, RepairStatus.Unknown,
                "l'emplacement du fichier d'échange n'a pas pu être lu.");
        if (fichiers.Count == 0)
            return new RepairFinding("PAGEFILE", Nom, "aucun", null, RepairStatus.Observed,
                "Windows ne déclare aucun fichier d'échange en service.");
        var chemins = string.Join(", ", fichiers.Select(f => f.Chemin));
        // Revue du 08/10/2026 : seuls les fichiers posés sur un disque dur sont nommés (cas mixte C: SSD, D: disque dur).
        var lents = string.Join(", ", fichiers.Where(f => f.TypeDeDisque == JeuxSurDisqueDur.DisqueDur).Select(f => f.Chemin));
        if (lents.Length > 0)
            return ssdPresent switch
            {
                true => new RepairFinding("PAGEFILE", Nom, lents, "sur un SSD", RepairStatus.Suspicious,
                    $"le fichier d'échange {lents} est sur un disque dur alors qu'un SSD interne est là : ce qui déborde de la "
                    + "mémoire s'y écrit et s'y relit bien plus lentement. Pour le déplacer : Paramètres système avancés, "
                    + "Performances, Paramètres, Avancé, Mémoire virtuelle, Modifier. DrDXT ne le déplace pas.",
                    RepairAction.Recommandation),
                false => new RepairFinding("PAGEFILE", Nom, lents, null, RepairStatus.Observed,
                    $"le fichier d'échange {lents} est sur un disque dur, et aucun SSD interne n'est vu sur ce PC : pas de meilleur endroit."),
                null => new RepairFinding("PAGEFILE", Nom, lents, null, RepairStatus.Unknown,
                    $"le fichier d'échange {lents} est sur un disque dur ; un disque de ce PC ne dit pas son type, DrDXT ne sait "
                    + "pas s'il y a un meilleur endroit : non jugé."),
            };
        if (fichiers.All(f => f.TypeDeDisque == JeuxSurDisqueDur.Ssd))
            return new RepairFinding("PAGEFILE", Nom, chemins, "sur un SSD", RepairStatus.ExpectedDefault,
                "le fichier d'échange est sur un SSD.");
        return new RepairFinding("PAGEFILE", Nom, chemins, null, RepairStatus.Unknown,
            "Windows ne dit pas le type du disque qui porte le fichier d'échange : non jugé.");
    }
}
