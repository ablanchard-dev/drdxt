using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>Ce qu'un gain CPU peut rendre au plus, une fois le mur GPU pris en compte.</summary>
/// <param name="PlafondPercent">Le gain d'images maximal, en pourcent. <c>0</c> quand le GPU est
/// deja sature : liberer du CPU ne rend alors rien.</param>
/// <param name="GpuBusyPercent">L'occupation GPU mesuree sur laquelle la borne est calculee.</param>
/// <param name="Lisible">Ce que la borne autorise a dire, en une phrase.</param>
public sealed record BorneDuGain(double PlafondPercent, double GpuBusyPercent, string Lisible);

/// <summary>
/// Le mur GPU : pourquoi rendre du CPU ne rend pas autant d'images qu'on croit. Sur la machine de reference, un cœur seul
/// tient 5 292 MHz, huit cœurs charges tombent a 3 698, et en partie 3 861 MHz ; le jeu est limité par le CPU (`gpuWait`
/// a 0-3 % sur quatre series), d'ou « lever la limite rendrait +25 a +30 % ». Faux : le GPU est occupe 86 a 95 % du
/// temps de trame, et a 90 % il ne peut dessiner que 11 % de plus, quelle que soit la frequence recuperee.
/// Un plafond, pas une prediction (« pas plus que X % ») : il suppose un travail GPU par image constant et parfaitement
/// empile, deux hypotheses optimistes. Il depend de la scene : les series mesurees le 09/09 (86, 87, 90, 91, 94 et 95 %) donnent
/// +16,3 % a +5,3 % ; c'est l'etendue qui est vraie.
/// </summary>
public static class PlafondGpu
{
    /// <summary>L'occupation sous laquelle le GPU n'est plus un mur (a 50 %, il dessinerait plus du double). Convention de
    /// lecture : elle choisit la phrase imprimee, pas ce qui est vrai.</summary>
    private const double MurEnVue = 50.0;

    /// <summary>Le nombre suit la langue de l'App (<see cref="Docteur.Langue"/>), pas celle du processus : une virgule en
    /// français, un point en anglais, quelle que soit la langue de Windows.</summary>
    private static string Pct(double v) => v.ToString("0.#", Docteur.Langue);

    /// <summary>La borne pour une occupation GPU donnee, en pourcent.</summary>
    public static BorneDuGain Borner(double gpuBusyPercent)
    {
        if (gpuBusyPercent is <= 0 or > 100)
            return new BorneDuGain(0, gpuBusyPercent,
                Docteur.T("PG.Hors"));

        if (gpuBusyPercent >= 100)
            return new BorneDuGain(0, gpuBusyPercent,
                Docteur.T("PG.Saturee"));

        // A gpuBusy = 90 %, le GPU dessine 0,9 image par unite de temps de trame ; il peut au plus
        // en dessiner 1,0, soit 1/0,9 = +11,1 %.
        var plafond = (100.0 / gpuBusyPercent - 1.0) * 100.0;

        // Sur le compositeur de bureau (GPU a 1,4 %), on lirait « pas plus de 6 945,4 % d'images de plus », vrai et lu
        // comme une promesse. Sous MurEnVue, ce qui limite est ailleurs (cadence de l'application, cap, vsync) : on dit
        // l'occupation sans chiffre de gain.
        if (gpuBusyPercent < MurEnVue)
            return new BorneDuGain(plafond, gpuBusyPercent,
                Docteur.T("PG.PasElle", Pct(gpuBusyPercent)));

        return new BorneDuGain(plafond, gpuBusyPercent,
            Docteur.T("PG.Borne", Pct(gpuBusyPercent), Pct(plafond)));
    }

    /// <summary>
    /// La borne tiree d'une mesure de scan. L'occupation vient de <c>BenchmarkResult.GpuBusyPercent</c> (celle du verdict
    /// de goulot, appariee), jamais recalculee ici. Au-dessus de 100 %, ramenee a 100 : le travail GPU se chevauche d'une
    /// trame a l'autre, le GPU ne decroche jamais, et se taire enverrait le joueur chercher des gains cote CPU.
    /// </summary>
    /// <returns><c>null</c> quand rien n'a ete mesure. Jamais une borne tiree d'une valeur
    /// absente.</returns>
    public static BorneDuGain? DepuisLaMesure(double? gpuBusyPercent)
        => gpuBusyPercent is not > 0 ? null : Borner(Math.Min(gpuBusyPercent.Value, 100.0));

    /// <summary>La phrase du scan, une seule source pour l'écran et le rapport. Le rappel de scène reste collé à la borne :
    /// sans lui, elle deviendrait une promesse.</summary>
    public static string? PhraseDuScan(double? gpuBusyPercent)
        => DepuisLaMesure(gpuBusyPercent) is { } borne
            ? borne.Lisible + Docteur.T("PG.Scene")
            : null;

    /// <summary>L'etendue des bornes sur plusieurs series, a publier plutot qu'une borne d'une seule scene.</summary>
    /// <returns>La borne la plus basse et la plus haute, dans cet ordre. Deux fois zero quand
    /// aucune serie n'est exploitable : jamais une borne inventee.</returns>
    public static (BorneDuGain Basse, BorneDuGain Haute) Etendue(IEnumerable<double>? gpuBusyPercents)
    {
        var bornes = (gpuBusyPercents ?? [])
            .Where(p => p is > 0 and <= 100)
            .Select(Borner)
            .OrderBy(b => b.PlafondPercent)
            .ToList();

        return bornes.Count == 0
            ? (new BorneDuGain(0, 0, Docteur.T("PG.AucuneSerie")),
               new BorneDuGain(0, 0, Docteur.T("PG.AucuneSerie")))
            : (bornes[0], bornes[^1]);
    }
}
