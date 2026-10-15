namespace DrDxt.Core;

/// <summary>
/// Ce que la limite de puissance fait vraiment pendant une
/// partie normale, lu par le pilote. La part du temps où la carte est bridée par sa limite (bit SwPowerCap), et sa fréquence
/// bridée contre libre. Un levier qui abaisse la limite ne peut coûter que ce temps-là, à cet écart-là : bridée 20 % du
/// temps à −0,5 %, c'est ≈ −0,1 % sur la partie, sous le bruit des images. Une lecture « au repos » (GpuIdle) ne compte pas.
/// </summary>
/// <param name="BridesChaleur">Les lectures où le pilote dit la carte freinée par la chaleur
/// (SwThermalSlowdown ou HwThermalSlowdown) ; 0 dans les parties écrites avant.</param>
/// <param name="LecturesActives">Le dénominateur de <see cref="PourcentChaleur"/> (toutes les
/// lectures où la carte n'est pas inactive, celles freinées par la chaleur comprises) ; 0 dans les parties écrites avant.</param>
public sealed record MecanismeCarte(int Echantillons, int BridesPuissance, double? MhzBride, double? MhzLibre, int BridesChaleur = 0,
    int LecturesActives = 0)
{
    private const ClocksEventReason Chaleur = ClocksEventReason.ThermiqueLogiciel | ClocksEventReason.ThermiqueMateriel;

    public static MecanismeCarte? Depuis(IEnumerable<HardwareReading> lus)
    {
        var lues = lus.Where(r => r.Raisons is { } raisons && r.GpuClockMhz is not null
                                  && (raisons & ClocksEventReason.GpuInactif) == 0).ToList();
        // La chaleur se compte avant le filtre « en charge » ; une carte freinée par la chaleur baisse
        // fréquence et tension, donc puissance, et ces lectures-là (les plus graves) tombaient sous les 60 % du pic.
        var chaudes = lues.Count(r => (r.Raisons!.Value & Chaleur) != 0);
        // Seules les lectures en charge comptent. Un menu ou un chargement n'est pas « inactif »
        // pour le pilote, mais il tourne bas et sans bridage : compté « libre », il tirerait la fréquence libre vers le bas et
        // sous-estimerait la perte. En charge = au moins 60 % de la puissance la plus haute lue ; sans puissance lue, gardée.
        var pic = lues.Select(r => r.GpuPowerW).OfType<double>().DefaultIfEmpty(0).Max();
        var valides = lues.Where(r => r.GpuPowerW is not { } w || pic <= 0 || w >= 0.6 * pic).ToList();
        if (valides.Count == 0) return null;
        var brides = valides.Where(r => (r.Raisons!.Value & ClocksEventReason.PlafondPuissanceLogiciel) != 0).ToList();
        var libres = valides.Except(brides).ToList();
        return new MecanismeCarte(valides.Count, brides.Count,
            Mediane(brides.Select(r => r.GpuClockMhz!.Value)), Mediane(libres.Select(r => r.GpuClockMhz!.Value)),
            chaudes, lues.Count);
    }

    /// <summary>Les captures d'une partie mises ensemble : comptes additionnés, fréquences à leur médiane.</summary>
    public static MecanismeCarte? Cumuler(IEnumerable<MecanismeCarte?> morceaux)
    {
        var lus = morceaux.OfType<MecanismeCarte>().ToList();
        if (lus.Count == 0) return null;
        return new MecanismeCarte(lus.Sum(m => m.Echantillons), lus.Sum(m => m.BridesPuissance),
            Mediane(lus.Select(m => m.MhzBride).OfType<double>()), Mediane(lus.Select(m => m.MhzLibre).OfType<double>()),
            lus.Sum(m => m.BridesChaleur), lus.Sum(m => m.LecturesActives));
    }

    /// <summary>Part du temps freinée par la chaleur, en %.</summary>
    public double PourcentChaleur => (LecturesActives > 0 ? LecturesActives : Echantillons) is var n and > 0 ? 100.0 * BridesChaleur / n : 0;

    /// <summary>Part du temps bridée par la puissance, en %.</summary>
    public double PourcentBride => Echantillons == 0 ? 0 : 100.0 * BridesPuissance / Echantillons;

    /// <summary>La fréquence perdue sur la partie, en % : part bridée × écart bridé / libre. 0 si jamais bridée ; <c>null</c>
    /// quand un des deux côtés n'a pas été vu (bridée tout le temps : l'écart ne se lit pas).</summary>
    public double? PerteEstimeePourcent => BridesPuissance == 0 ? 0
        : MhzBride is { } b && MhzLibre is { } l && l > 0 ? PourcentBride * Math.Max(0, 1 - b / l) : null;

    private static double? Mediane(IEnumerable<double> valeurs)
    {
        var v = valeurs.Order().ToList();
        return v.Count == 0 ? null : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
