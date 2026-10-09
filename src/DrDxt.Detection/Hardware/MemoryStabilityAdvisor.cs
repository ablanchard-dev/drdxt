using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// « OPTIMAL » est un jugement de qualite sur un overclock qu'on n'a pas teste.
///
/// `LeverCatalog` rend `Optimal` des que XMP/EXPO est actif. C'est vrai en performance et muet sur
/// la stabilite : un profil XMP est un overclock memoire, il peut ne pas tenir, et le produit n'a
/// aucun moyen de le tester. Tant qu'il ne dit rien, un client dont la machine gele lit « memoire :
/// rien a corriger » et cherche ailleurs.
///
/// ══ ce que ce conseil n'a pas le droit de faire : accuser ══
/// `Kernel-Power 41` signifie « Windows a redemarre sans arret propre ». **Une extinction au bouton
/// ou a la prise se logue exactement pareil.** Ce projet a deja fait cette erreur une fois, en juin,
/// et l'a corrigee : ecrire « ta machine plante N fois » a partir de ce compte est une
/// surinterpretation, pas une mesure.
///
/// Le conseil pose donc deux faits lus (un profil est actif, la machine a redemarre sans arret
/// propre) et **un UNKNOWN nomme** (nous n'avons pas teste ce profil). Il propose la seule
/// experience qui tranche : repasser en JEDEC quelques jours. Il ne chiffre rien -- classe `NON CHIFFRE`.
///
/// 16/09/2026 : il ecrivait « nous ne savons pas distinguer un gel d'une extinction au bouton ».
/// Faux : `PowerButtonTimestamp` le dit. Les appuis longs sur le bouton sont desormais exclus A LA
/// Lecture (<see cref="ArretBrutal.HorsBouton"/>), et le detail (ecrans bleus, codes) est dit par
/// l'audit de stabilite.
///
/// ══ pourquoi pas sur un profil sain ══
/// Sans le moindre redemarrage sale, « XMP est un overclock non teste » est vrai et inutile : un
/// avertissement permanent s'apprend a etre ignore, et le jour ou il a raison plus personne ne le
/// voit. Le conseil n'existe donc que quand les deux faits sont reunis.
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
        // Dans les deux cas on se tait -- on n'accuse pas un profil dont on ignore l'existence.
        if (xmpExpo != TriState.On) return Array.Empty<MemoryTopologyGuidance>();

        // `null` = jamais lu, liste vide = lu et propre. Les deux se taisent ici, et une mutation
        // du 06/09 l'a prouve : separer les deux cas dans ce conseiller donnait un mutant
        // EQUIVALENT -- une garde qui a l'air de defendre quelque chose et ne defend rien. La
        // distinction qui compte vit chez le LECTEUR (`SettingsStateReader`), ou traduire un
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
