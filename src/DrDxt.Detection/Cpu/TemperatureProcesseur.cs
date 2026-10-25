namespace DrDxt.Detection.Cpu;

/// <summary>La température du processeur, par le premier étage qui répond ; chacun passe la main au suivant
/// (même une source qui plante) : HWiNFO s'il publie, PawnIO lu directement (App élevée), le service DrDXT par son tube (App
/// sans droits, ou lecture directe ratée), d'abord pour Intel puis pour AMD. Les étages AMD ne lisent rien hors d'une
/// génération validée en vie (LecturesAmdProfondes.ValideesEnVie). Chaque lecture porte le nom de sa source ; aucune ⇒
/// null, et le docteur dit « non lue », jamais « saine ». Un test casse si un étage disparaît.</summary>
public static class TemperatureProcesseur
{
    public static readonly IReadOnlyList<(string Nom, Func<TemperatureHwinfo.Lecture?> Lire)> Chaine =
    [
        ("HWiNFO", TemperatureHwinfo.Lire),
        ("PawnIO direct", TemperaturePawnIo.LireDirecte),
        ("service DrDXT", TemperaturePawnIo.LireParLeService),
        ("PawnIO AMD direct", LecturesAmd.TemperatureDirecte),
        ("service DrDXT, AMD", LecturesAmd.TemperatureParLeService),
    ];

    public static TemperatureHwinfo.Lecture? Lire() => Premiere(Chaine.Select(c => c.Lire));

    /// <summary>Pur : la première lecture rendue ; une source qui lève passe la main ; aucune ⇒ null.</summary>
    public static TemperatureHwinfo.Lecture? Premiere(IEnumerable<Func<TemperatureHwinfo.Lecture?>> sources)
    {
        foreach (var source in sources)
        {
            try { if (source() is { } lue) return lue; }
            catch (Exception) { /* cette source passe la main à la suivante */ }
        }
        return null;
    }
}
