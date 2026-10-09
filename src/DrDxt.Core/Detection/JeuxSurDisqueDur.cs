namespace DrDxt.Core;

/// <summary>Un dossier de jeux (bibliothèque Steam, jeu Epic) et le type du disque qui le porte, lu par Windows
/// (MSFT_PhysicalDisk.MediaType : 3 = disque dur, 4 = SSD, 0 = non dit).</summary>
public sealed record DossierDeJeux(string Lanceur, string Chemin, int TypeDeDisque);

/// <summary>
/// 07/10/2026 (chantier D5) : des jeux installés sur un disque dur se chargent bien plus lentement que sur un SSD
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
                "les bibliothèques de jeux (Steam, Epic, Battle.net, Riot, EA app, Ubisoft Connect) n'ont pas pu être lues : DrDXT ne sait pas sur quel disque sont tes jeux.");
        if (dossiers.Count == 0)
            return new RepairFinding("STOCKAGE", Nom, null, null, RepairStatus.Observed,
                "aucun jeu trouvé dans Steam, Epic, Battle.net, Riot, EA app ni Ubisoft Connect : DrDXT ne sait pas où sont tes "
                + "jeux (un autre lanceur n'est pas lu).");
        var lents = dossiers.Where(d => d.TypeDeDisque == DisqueDur).ToList();
        if (lents.Count > 0)
            return new RepairFinding("STOCKAGE", Nom, string.Join(", ", lents.Select(d => $"{d.Lanceur} : {d.Chemin}")), "un SSD",
                RepairStatus.Suspicious,
                $"{lents.Count} dossier(s) de jeux sur un disque dur ({string.Join(", ", lents.Select(d => d.Chemin))}) : les "
                + "chargements y sont bien plus longs que sur un SSD. Si tu as un SSD avec de la place, ton lanceur sait "
                + "déplacer un jeu (Steam : Propriétés du jeu, Fichiers installés, Déplacer ; Battle.net : réglages du jeu, "
                + "Déplacer le dossier d'installation). DrDXT ne déplace rien.",
                RepairAction.Recommandation);
        // Revue F-44 : rien de lu ne vaut pas « aucun » ; et seuls les dossiers lus sont dits sur un SSD.
        var nonDits = dossiers.Count(d => d.TypeDeDisque is not (DisqueDur or Ssd));
        if (nonDits == dossiers.Count)
            return new RepairFinding("STOCKAGE", Nom, null, null, RepairStatus.Unknown,
                "Windows ne dit pas le type des disques qui portent tes jeux : non jugé.");
        return new RepairFinding("STOCKAGE", Nom, "aucun", null, RepairStatus.ExpectedDefault,
            $"{dossiers.Count - nonDits} dossier(s) de jeux sur un SSD"
            + (nonDits > 0 ? $", {nonDits} sur un disque dont Windows ne dit pas le type (non jugé)." : "."));
    }
}
