namespace DrDxt.Core;

/// <summary>
/// Un fichier d'échange sur un disque dur alors qu'un
/// SSD est là ralentit tout ce qui déborde de la mémoire (chargements, retours au bureau). Lecture seule : DrDXT ne le
/// déplace pas ; le geste est dans Windows. Le type vient de MSFT_PhysicalDisk.MediaType (3 = disque dur, 4 = SSD,
/// 0 = non dit) ; un type non dit n'est jamais jugé.
/// </summary>
public static class FichierDEchangeSurDisqueDur
{
    public const string Nom = "emplacement";

    /// <param name="fichiers">Chaque fichier d'échange (chemin, type du disque qui le porte) ; null = non lu.</param>
    /// <param name="ssdPresent">Un SSD interne est-il présent sur ce PC ? <c>null</c> : un disque n'a pas dit son type, rien
    /// N'est affirmé. Un SSD branché en USB ne
    /// compte pas : on n'y conseille pas le fichier d'échange.</param>
    public static RepairFinding Juger(IReadOnlyList<(string Chemin, int TypeDeDisque)>? fichiers, bool? ssdPresent)
    {
        if (fichiers is null)
            return new RepairFinding("PAGEFILE", Nom, null, null, RepairStatus.Unknown,
                Docteur.T("FE.NonLu"));
        if (fichiers.Count == 0)
            return new RepairFinding("PAGEFILE", Nom, Docteur.T("FE.Aucun"), null, RepairStatus.Observed,
                Docteur.T("FE.AucunEnService"));
        var chemins = string.Join(", ", fichiers.Select(f => f.Chemin));
        // Seuls les fichiers posés sur un disque dur sont nommés (cas mixte C: SSD, D: disque dur).
        var lents = string.Join(", ", fichiers.Where(f => f.TypeDeDisque == JeuxSurDisqueDur.DisqueDur).Select(f => f.Chemin));
        if (lents.Length > 0)
            return ssdPresent switch
            {
                true => new RepairFinding("PAGEFILE", Nom, lents, Docteur.T("FE.SurSsd"), RepairStatus.Suspicious,
                    Docteur.T("FE.DisqueDurAvecSsd", lents),
                    RepairAction.Recommandation),
                false => new RepairFinding("PAGEFILE", Nom, lents, null, RepairStatus.Observed,
                    Docteur.T("FE.DisqueDurSansSsd", lents)),
                null => new RepairFinding("PAGEFILE", Nom, lents, null, RepairStatus.Unknown,
                    Docteur.T("FE.TypeInconnu", lents)),
            };
        if (fichiers.All(f => f.TypeDeDisque == JeuxSurDisqueDur.Ssd))
            return new RepairFinding("PAGEFILE", Nom, chemins, Docteur.T("FE.SurSsd"), RepairStatus.ExpectedDefault,
                Docteur.T("FE.Ssd"));
        return new RepairFinding("PAGEFILE", Nom, chemins, null, RepairStatus.Unknown,
            Docteur.T("FE.NeDitPas"));
    }
}
