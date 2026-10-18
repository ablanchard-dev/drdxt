using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>Ce qu'un gain CPU peut rendre au plus, une fois le mur GPU pris en compte.</summary>
/// <param name="PlafondPercent">Le gain d'images maximal, en pourcent. <c>0</c> quand le GPU est
/// deja sature : liberer du CPU ne rend alors rien.</param>
/// <param name="GpuBusyPercent">L'occupation GPU mesuree sur laquelle la borne est calculee.</param>
/// <param name="Lisible">Ce que la borne autorise a dire, en une phrase.</param>
public sealed record BorneDuGain(double PlafondPercent, double GpuBusyPercent, string Lisible);

/// <summary>
/// Le mur GPU : pourquoi rendre du CPU ne rend pas autant d'images qu'on croit.
///
/// ══ la question a laquelle ce type repond ══
/// Sur la machine de reference, le processeur est bride en regime soutenu : un cœur seul tient
/// 5 292 MHz, huit cœurs charges tombent a 3 698, et en partie les cœurs qui travaillent tournent
/// a 3 861 MHz. Le jeu est limité par le CPU (`gpuWait` mesure a 0-3 % sur quatre series). La tentation
/// est immediate : « lever la limite rendrait +25 a +30 % d'images ».
///
/// C'est faux, et le meme jeu de mesures le dit. Le GPU est occupe 86 a 95 % du temps de
/// trame. Rendre plus d'images demande au GPU d'en dessiner plus, et il n'a pas 30 % de marge : a
/// 90 % d'occupation, il ne peut en dessiner que 11 % de plus avant d'etre sature a son tour.
/// Le gain CPU se heurte a ce mur, quelle que soit la frequence recuperee.
///
/// ══ ce que la borne est, et n'est pas ══
/// C'est un plafond, pas une prediction. Elle dit « pas plus que X % », jamais « X % ». Le gain
/// reel sera plus petit : elle suppose que le travail GPU par image ne change pas et qu'il
/// s'empile parfaitement, deux hypotheses optimistes.
///
/// Et elle depend de la scene. Les series mesurees le 09/09 rendent 86, 87, 90, 91, 94 et 95 %
/// d'occupation, soit des plafonds de +16,3 % a +5,3 % selon le moment de la partie. Une
/// borne unique serait plus commode et fausse ; c'est l'etendue qui est vraie.
/// </summary>
public static class PlafondGpu
{
    /// <summary>L'occupation en dessous de laquelle le GPU n'est plus un mur : a 50 %, il peut
    /// deja dessiner plus du double des images, donc il ne borne rien qu'un joueur puisse
    /// atteindre. Convention de lecture : elle ne change pas ce qui est vrai, seulement la phrase
    /// qu'on imprime.</summary>
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

        // Sur une capture reelle du compositeur de bureau (GPU occupe a 1,4 %), la phrase sortirait :
        // « ne peut pas donner plus de 6 945,4 % d'images de plus ». Vrai, inutile, et lu comme une
        // promesse a quatre chiffres. En dessous de MurEnVue, le GPU peut deja dessiner plus du double : il ne borne
        // plus rien, et ce qui limite les images est ailleurs (cadence de l'application, cap,
        // vsync). On dit alors l'occupation sans chiffre de gain.
        //
        // Ce seuil est une convention de lecture, pas une mesure : les deux phrases sont vraies
        // a toute occupation, seul le choix de celle qu'on imprime change. Il est ecrit ici pour
        // qu'on puisse le contester, au lieu de vivre dans un `if` sans nom.
        if (gpuBusyPercent < MurEnVue)
            return new BorneDuGain(plafond, gpuBusyPercent,
                Docteur.T("PG.PasElle", Pct(gpuBusyPercent)));

        return new BorneDuGain(plafond, gpuBusyPercent,
            Docteur.T("PG.Borne", Pct(gpuBusyPercent), Pct(plafond)));
    }

    /// <summary>
    /// La borne tiree d'une mesure de scan : l'occupation ne se devine pas, elle se calcule.
    ///
    /// L'occupation n'est pas recalculee ici : elle vient de <c>BenchmarkResult.GpuBusyPercent</c>,
    /// qui la publie deja pour le verdict de goulot. Une deuxieme arithmetique finirait par
    /// diverger de la premiere, et risquerait de perdre l'appariement des trames.
    ///
    /// Au-dessus de 100 %, on ramene a 100 plutot que de refuser de conclure : le travail GPU
    /// peut se chevaucher d'une trame a l'autre, et un rapport superieur a 1 veut dire que le GPU
    /// ne decroche jamais. Se taire dans ce cas laisserait un joueur dont le GPU est le mur
    /// chercher des gains cote CPU : exactement le temps perdu que ce type existe pour eviter.
    /// </summary>
    /// <returns><c>null</c> quand rien n'a ete mesure. Jamais une borne tiree d'une valeur
    /// absente.</returns>
    public static BorneDuGain? DepuisLaMesure(double? gpuBusyPercent)
        => gpuBusyPercent is not > 0 ? null : Borner(Math.Min(gpuBusyPercent.Value, 100.0));

    /// <summary>La phrase du scan, une seule source pour l'écran et le rapport. Le rappel de scène
    /// est collé à la borne et n'en part jamais : une borne lue sans lui deviendrait une promesse. L'écran
    /// ajoutait « . Cette borne » à une phrase déjà close (« en pratique moins.. Cette borne »).</summary>
    public static string? PhraseDuScan(double? gpuBusyPercent)
        => DepuisLaMesure(gpuBusyPercent) is { } borne
            ? borne.Lisible + Docteur.T("PG.Scene")
            : null;

    /// <summary>L'etendue des bornes sur plusieurs series. C'est elle qu'il faut publier : une
    /// borne unique tiree d'une seule scene serait plus commode et fausse.</summary>
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
