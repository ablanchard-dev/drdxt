namespace DrDxt.Core;

/// <summary>Un dossier de jeux (bibliothèque Steam, jeu Epic) et le type du disque qui le porte, lu par Windows
/// (MSFT_PhysicalDisk.MediaType : 3 = disque dur, 4 = SSD, 0 = non dit).</summary>
public sealed record DossierDeJeux(string Lanceur, string Chemin, int TypeDeDisque);

/// <summary>
/// Des jeux installés sur un disque dur se chargent bien plus lentement que sur un SSD
/// (cartes, textures, retours au menu). Lecture seule : DrDXT ne déplace rien ; Steam et Epic savent déplacer une
/// bibliothèque eux-mêmes. Le temps gagné n'est pas chiffré (il dépend du jeu). Type de disque non dit (0) : rien n'est
/// affirmé.
/// </summary>
public static class JeuxSurDisqueDur
{
    public const int DisqueDur = 3, Ssd = 4;

    public static RepairFinding Juger(IReadOnlyList<DossierDeJeux>? dossiers)
    {
        const string Nom = "jeux sur un disque dur";
        if (dossiers is null)
            return new RepairFinding("STOCKAGE", Nom, null, null, RepairStatus.Unknown,
                Docteur.T("JD.NonLues"));
        if (dossiers.Count == 0)
            return new RepairFinding("STOCKAGE", Nom, null, null, RepairStatus.Observed,
                Docteur.T("JD.Aucun"));
        var lents = dossiers.Where(d => d.TypeDeDisque == DisqueDur).ToList();
        // Ranger des jeux sur un disque dur est un choix de place ; on ne sait pas lesquels se jouent : à regarder.
        if (lents.Count > 0)
            return new RepairFinding("STOCKAGE", Nom, string.Join(", ", lents.Select(d => $"{d.Lanceur}{Docteur.DeuxPoints}{d.Chemin}")), Docteur.T("JD.UnSsd"),
                RepairStatus.NonDefault,
                Docteur.T("JD.Lents", lents.Count, string.Join(", ", lents.Select(d => d.Chemin))),
                RepairAction.Recommandation);
        // Rien de lu ne vaut pas « aucun » ; et seuls les dossiers lus sont dits sur un SSD.
        var nonDits = dossiers.Count(d => d.TypeDeDisque is not (DisqueDur or Ssd));
        if (nonDits == dossiers.Count)
            return new RepairFinding("STOCKAGE", Nom, null, null, RepairStatus.Unknown,
                Docteur.T("JD.NeDitPas"));
        return new RepairFinding("STOCKAGE", Nom, Docteur.T("JD.Aucune"), null, RepairStatus.ExpectedDefault,
            Docteur.T("JD.SurSsd", dossiers.Count - nonDits)
            + (nonDits > 0 ? Docteur.T("JD.NonDits", nonDits) : "."));
    }
}
