namespace DrDxt.Detection.Display;

/// <summary>Un conseil sur la consommation au repos, avec sa source, comme tous les conseils de ce depot.</summary>
public sealed record IdlePowerGuidance(string Id, string MessageKey, string SourceUrl);

/// <summary>
/// La consommation au repos avec des ecrans a des frequences differentes : la machine de reference tire 55,9 W au repos
/// (carte plafonnee a 115 W, ecrans a 200 et 144 Hz), le seul effet du depot qui joue 24 h sur 24. Source
/// (`docs/LEVIERS-REPOS-ET-RESEAU.md`, R-01 a R-03) : la memoire video ne descend pas a son etat de repos ; NVIDIA decrit
/// ce comportement comme voulu et conseille de baisser la frequence hors jeu.
///   • Jamais chiffre : R-02 rapporte 30-40 W vers 21,5 W, jusqu'a 95 W vers 35 W, mesure sur d'autres machines.
///   • Jamais l'ecran principal : baisser sa frequence couterait des images (le defaut de
///     `Finding.Display.RefreshBelowMax`).
/// Pur ; ce qui n'a pas ete lu ne conseille rien.
/// </summary>
public static class IdlePowerAdvisor
{
    /// <summary>La source qui etablit le phenomene, et le fait que le constructeur l'assume.</summary>
    public const string SourceUrl =
        "https://www.nvidia.com/en-us/geforce/forums/discover/233230/very-high-idle-clocks-on-gtx-1080-with-multiple-displays/";

    /// <param name="attachedRefreshHz">La frequence de chaque ecran attache. <c>null</c> ou vide = jamais lu ; un 0 = une
    /// lecture ratee. Dans les deux cas, silence.</param>
    public static IReadOnlyList<IdlePowerGuidance> Advise(IReadOnlyList<int>? attachedRefreshHz)
    {
        if (attachedRefreshHz is null || attachedRefreshHz.Count < 2)
            return Array.Empty<IdlePowerGuidance>();

        // Un seul ecran illisible rend le verdict illisible (comme la topologie memoire face a un emplacement non nomme).
        if (attachedRefreshHz.Any(hz => hz <= 0))
            return Array.Empty<IdlePowerGuidance>();

        // La difference compte, pas le nombre d'ecrans : deux panneaux a la meme frequence ne declenchent rien.
        if (attachedRefreshHz.Distinct().Count() < 2)
            return Array.Empty<IdlePowerGuidance>();

        // Un seul constat, quel que soit le nombre d'ecrans.
        return new[]
        {
            new IdlePowerGuidance(
                "display.mixed_refresh_idle_power", "Finding.Display.MixedRefreshIdlePower", SourceUrl),
        };
    }
}
