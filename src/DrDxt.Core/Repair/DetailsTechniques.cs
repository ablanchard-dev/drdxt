namespace DrDxt.Core;

/// <summary>
/// L'information d'expert sans geste : lue, utile à qui sait la lire, sans rien à faire pour le joueur. Tant qu'elle ne
/// signale pas de problème, l'écran la range dans « Détails techniques », replié ; un de ces constats suspect ou pire, ou
/// porteur d'un geste, reste en haut comme les autres, et un non-lu reste dans « Pas pu lire » : replié, un angle mort
/// passerait pour une lecture saine. Le tri (<see cref="RepairTriage"/>), le journal et le rapport
/// exporté ne changent pas : ils gardent tout.
/// </summary>
public static class DetailsTechniques
{
    /// <summary>Les familles, nommées une à une : un constat n'y entre que s'il est listé ici. Nom null = tout le domaine
    /// (les traces datées du registre portent un nom par clé).</summary>
    private static readonly (string Domaine, string? Nom)[] Familles =
    [
        ("CPU", "etats de veille"),
        ("CPU", "horloge haute resolution"),
        ("SECURITE", "attenuations du processeur"),
        ("CPU", "caches L3"),
        ("LATENCE", "recensement par coeur"),
        (TracesDOptimiseurs.Domaine, null),
        ("CPU", "generation AMD"),
        (RadarDesRegressions.Domaine, RadarDesRegressions.Nom),
    ];

    /// <summary>Pur : ce constat va-t-il dans « Détails techniques » ? Jamais un problème, un geste ni un non-lu ; le radar
    /// des régressions seulement quand sa table est vide.</summary>
    public static bool EnFait(RepairFinding c)
        => c.Statut is not (RepairStatus.Suspicious or RepairStatus.KnownHarmful or RepairStatus.Unknown)
           && c.Action == RepairAction.Aucune
           && Familles.Any(f => f.Domaine == c.Domaine && (f.Nom is null || f.Nom == c.Nom))
           && (c.Nom != RadarDesRegressions.Nom || RadarDesRegressions.TableVide(c));
}
