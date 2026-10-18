namespace DrDxt.Detection.Cpu;

/// <summary>La température du processeur, par la première source qui répond : HWiNFO s'il publie,
/// sinon PawnIO s'il est installé. Chaque lecture porte le nom de sa source ; aucune des deux ⇒ null, et le docteur dit
/// « non lue », jamais « saine ».
/// Une chaîne nommée de trois étages, chacun passe la main
/// au suivant (même une source qui plante) : HWiNFO, PawnIO lu directement (App élevée), le service DrDXT par son tube (App
/// sans droits, ou lecture directe ratée). Un test casse si un étage disparaît.</summary>
public static class TemperatureProcesseur
{
    public static readonly IReadOnlyList<(string Nom, Func<TemperatureHwinfo.Lecture?> Lire)> Chaine =
    [
        ("HWiNFO", TemperatureHwinfo.Lire),
        ("PawnIO direct", TemperaturePawnIo.LireDirecte),
        ("service DrDXT", TemperaturePawnIo.LireParLeService),
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
