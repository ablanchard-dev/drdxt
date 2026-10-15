namespace DrDxt.Detection.Display;

/// <summary>Un conseil sur la consommation au repos. Il porte sa source, comme tous les
/// conseils de ce depot : sans elle, il devient une opinion des qu'il quitte ce fichier.</summary>
public sealed record IdlePowerGuidance(string Id, string MessageKey, string SourceUrl);

/// <summary>
/// La consommation au repos, quand plusieurs ecrans tournent a des frequences differentes.
///
/// La machine de reference tire **55,9 W au repos** pour une
/// carte plafonnee a 115 W, avec deux ecrans a 200 et 144 Hz. Aucun levier du produit ne regardait
/// ca, et c'est le seul effet du depot qui joue 24 h sur 24 au lieu des seules heures de jeu.
///
/// La cause est sourcee (`docs/LEVIERS-REPOS-ET-RESEAU.md`, R-01 a R-03) : des frequences
/// differentes empechent la memoire video de descendre dans son etat de repos ; l'horloge reste au
/// palier haut et la consommation avec. **NVIDIA decrit ce comportement comme voulu** et conseille
/// elle-meme de baisser la frequence hors jeu.
///
/// ══ ce que ce conseil n'a pas le droit de faire ══
///   • Chiffrer. R-02 rapporte 30-40 W vers 21,5 W, et jusqu'a 95 W vers 35 W dans un cas extreme
///     (mesures sur d'autres machines, avec d'autres cartes et d'autres ecrans). Les afficher ici
///     comme un gain promis serait l'estimation que ce produit a retiree de sa vitrine.
///   • Viser l'ecran principal. Baisser sa frequence coute des images en jeu : ce serait creer de
///     nos mains le defaut que `Finding.Display.RefreshBelowMax` denonce.
///
/// Pur : aucune I/O, aucun etat. Ce qui n'a pas ete lu ne conseille rien.
/// </summary>
public static class IdlePowerAdvisor
{
    /// <summary>La source qui etablit le phenomene, et le fait que le constructeur l'assume.</summary>
    public const string SourceUrl =
        "https://www.nvidia.com/en-us/geforce/forums/discover/233230/very-high-idle-clocks-on-gtx-1080-with-multiple-displays/";

    /// <param name="attachedRefreshHz">La frequence de chaque ecran attache. <c>null</c> ou vide =
    /// jamais lu ; un 0 dans la liste = une lecture ratee, jamais un ecran a 0 Hz. Dans les deux
    /// cas on se tait : une lecture incomplete ne permet pas de dire que les frequences different.
    /// </param>
    public static IReadOnlyList<IdlePowerGuidance> Advise(IReadOnlyList<int>? attachedRefreshHz)
    {
        if (attachedRefreshHz is null || attachedRefreshHz.Count < 2)
            return Array.Empty<IdlePowerGuidance>();

        // Un seul ecran illisible rend le verdict illisible : on ne compare pas des frequences
        // dont l'une manque. Meme regle que la topologie memoire face a un emplacement non nomme.
        if (attachedRefreshHz.Any(hz => hz <= 0))
            return Array.Empty<IdlePowerGuidance>();

        // C'est la difference qui empeche la memoire de descendre, pas le nombre d'ecrans : deux
        // panneaux a la meme frequence ne declenchent rien.
        if (attachedRefreshHz.Distinct().Count() < 2)
            return Array.Empty<IdlePowerGuidance>();

        // Un seul constat, quel que soit le nombre d'ecrans concernes : le client n'a pas besoin
        // qu'on lui repete la meme cause une fois par ecran.
        return new[]
        {
            new IdlePowerGuidance(
                "display.mixed_refresh_idle_power", "Finding.Display.MixedRefreshIdlePower", SourceUrl),
        };
    }
}
