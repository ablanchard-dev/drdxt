namespace DrDxt.Core;

/// <summary>
/// J-MECA (08/10/2026, le mainteneur : « ne plus jamais jouer pour DrDXT ») : ce que la limite de puissance fait VRAIMENT pendant une
/// partie normale, lu par le pilote. La part du temps où la carte est bridée par sa limite (bit SwPowerCap), et sa fréquence
/// bridée contre libre. Un levier qui abaisse la limite ne peut coûter que ce temps-là, à cet écart-là : bridée 20 % du
/// temps à −0,5 %, c'est ≈ −0,1 % sur la partie, sous le bruit des images. Une lecture « au repos » (GpuIdle) ne compte pas.
/// </summary>
public sealed record MecanismeCarte(int Echantillons, int BridesPuissance, double? MhzBride, double? MhzLibre)
{
    public static MecanismeCarte? Depuis(IEnumerable<HardwareReading> lus)
    {
        var lues = lus.Where(r => r.Raisons is { } raisons && r.GpuClockMhz is not null
                                  && (raisons & ClocksEventReason.GpuInactif) == 0).ToList();
        // MECA2 (revue du 09/10/2026) : seules les lectures EN CHARGE comptent. Un menu ou un chargement n'est pas « inactif »
        // pour le pilote, mais il tourne bas et sans bridage : compté « libre », il tirait la fréquence libre vers le bas et
        // sous-estimait la perte. En charge = au moins 60 % de la puissance la plus haute lue ; sans puissance lue, gardée.
        var pic = lues.Select(r => r.GpuPowerW).OfType<double>().DefaultIfEmpty(0).Max();
        var valides = lues.Where(r => r.GpuPowerW is not { } w || pic <= 0 || w >= 0.6 * pic).ToList();
        if (valides.Count == 0) return null;
        var brides = valides.Where(r => (r.Raisons!.Value & ClocksEventReason.PlafondPuissanceLogiciel) != 0).ToList();
        var libres = valides.Except(brides).ToList();
        return new MecanismeCarte(valides.Count, brides.Count,
            Mediane(brides.Select(r => r.GpuClockMhz!.Value)), Mediane(libres.Select(r => r.GpuClockMhz!.Value)));
    }

    /// <summary>Les captures d'une partie mises ensemble : comptes additionnés, fréquences à leur médiane.</summary>
    public static MecanismeCarte? Cumuler(IEnumerable<MecanismeCarte?> morceaux)
    {
        var lus = morceaux.OfType<MecanismeCarte>().ToList();
        if (lus.Count == 0) return null;
        return new MecanismeCarte(lus.Sum(m => m.Echantillons), lus.Sum(m => m.BridesPuissance),
            Mediane(lus.Select(m => m.MhzBride).OfType<double>()), Mediane(lus.Select(m => m.MhzLibre).OfType<double>()));
    }

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
