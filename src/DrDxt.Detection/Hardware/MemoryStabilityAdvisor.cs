using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// « Optimal » est un jugement de qualite sur un overclock qu'on n'a pas teste.
///
/// `LeverCatalog` dit `Optimal` des que XMP/EXPO est actif, vrai en performance et muet sur la stabilite d'un overclock
/// memoire non teste. Quand une machine a XMP a redemarre sans arret propre, ce conseil pose ces deux faits lus et un
/// Unknown nomme (profil non teste), sans accuser (« ta machine plante N fois » serait une surinterpretation : les
/// appuis longs sont exclus par <see cref="ArretBrutal.HorsBouton"/>, le detail vit dans l'audit de stabilite), et
/// propose l'experience qui tranche : repasser en JEDEC quelques jours. `NON CHIFFRE`. Jamais sur un profil sain : un
/// avertissement permanent s'apprend a etre ignore.
/// </summary>
public static class MemoryStabilityAdvisor
{
    /// <summary>Fenetre de lecture. **Choix produit, pas une mesure** : assez large pour couvrir
    /// plusieurs semaines de jeu, assez courte pour qu'un incident d'il y a six mois ne parle plus
    /// d'une machine reglee depuis. Aucune source ne la fixe, et c'est pour ca qu'elle est ici,
    /// nommee, plutot que cachee dans une comparaison.</summary>
    public const int WindowDays = 90;

    public const string SourceUrl = "docs/RAM-ETAT-DE-LART.md";

    public static IReadOnlyList<MemoryTopologyGuidance> Advise(
        TriState xmpExpo,
        IReadOnlyList<DateTimeOffset>? uncleanShutdowns,
        ChassisType chassis,
        DateTimeOffset now)
    {
        // Hors d'un fixe le profil memoire est verrouille par le firmware : le conseil serait
        // vrai et inapplicable. Meme gate que tous les autres constats memoire.
        if (chassis != ChassisType.Desktop) return Array.Empty<MemoryTopologyGuidance>();

        // Off ou Unknown : il n'y a pas d'overclock a suspecter, ou on ne sait pas s'il y en a un.
        // Dans les deux cas on se tait : on n'accuse pas un profil dont on ignore l'existence.
        if (xmpExpo != TriState.On) return Array.Empty<MemoryTopologyGuidance>();

        // `null` = jamais lu, liste vide = lu et propre. Les deux se taisent ici : separer les deux cas
        // dans ce conseiller donnerait un mutant equivalent, une garde qui a l'air de defendre
        // quelque chose et ne defend rien. La
        // distinction qui compte vit chez le lecteur (`SettingsStateReader`), ou traduire un
        // journal illisible en liste vide fait bel et bien tomber un test.
        var earliest = now.AddDays(-WindowDays);
        if (uncleanShutdowns is null || !uncleanShutdowns.Any(d => d >= earliest))
            return Array.Empty<MemoryTopologyGuidance>();

        return new[]
        {
            new MemoryTopologyGuidance(
                "memory.xmp_on_with_unclean_shutdowns", "Memory",
                "Finding.Memory.XmpOnWithUncleanShutdowns", SourceUrl),
        };
    }
}
