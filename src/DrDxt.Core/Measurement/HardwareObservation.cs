namespace DrDxt.Core;

/// <summary>Un instant de telemetrie materielle. <c>null</c> = pas lisible, jamais zero : une
/// carte qui ne rend pas ses watts n'en consomme pas zero.</summary>
public readonly record struct HardwareReading(
    double? GpuPowerW,
    double? GpuTemperatureC,
    double? GpuClockMhz = null,
    /// <summary>Le bitmask publie par le pilote. <c>0</c> est une vraie valeur (rien ne
    /// bride), a ne jamais confondre avec <c>null</c>, qui veut dire « pas lisible ».</summary>
    ClocksEventReason? Raisons = null,
    /// <summary>Le point le plus chaud de la puce (ADLX le publie, NVML non) ; <c>null</c> = pas lisible.</summary>
    double? GpuPointChaudC = null,
    /// <summary>Le ventilateur de la carte, en tours par minute ; <c>null</c> = pas lisible.</summary>
    int? VentilateurTrMin = null);

/// <summary>Ce qui sait lire le materiel pendant une passe. Une couture, pour que la boucle se
/// teste sans carte graphique.</summary>
public interface IHardwareProbe
{
    /// <summary>Lit l'etat courant. Ne leve jamais : une lecture ratee rend des <c>null</c>.</summary>
    HardwareReading Lire();
}

/// <summary>
/// Ce que le materiel a fait pendant la passe, sur la meme fenetre que les frames : echantillonne pendant chaque passe,
/// comme le focus (lus apres, les watts decrivent une carte qui redescend deja). Pas une cause : « pendant les fenetres
/// mesurees, le GPU etait a 97 % d'occupation et 68 °C », jamais « c'est la temperature qui te coute des FPS ».
/// </summary>
/// <param name="Echantillons">Combien de lectures ont ete faites, y compris celles qui n'ont rien rendu. Zero = aucune
/// lecture, et les agregats valent <c>null</c>.</param>
/// <param name="PuissanceMedianeW">La mediane et non la moyenne : une seule lecture aberrante
/// pendant un changement d'etat tirerait une moyenne, pas une mediane.</param>
/// <param name="TemperatureMaxC">Le max et non la moyenne : un plafond thermique est un
/// evenement, et une moyenne le noie.</param>
public sealed record HardwareObservation(
    int Echantillons,
    double? PuissanceMedianeW,
    double? TemperatureMaxC,
    /// <summary>Frequence mediane de la passe. La mediane parce qu'une carte NVIDIA boost et
    /// redescend en permanence : une moyenne melangerait les deux regimes.</summary>
    double? FrequenceMedianeMhz = null,
    /// <summary>Frequence minimale. C'est elle qui porte un decrochage : la mediane le noie.</summary>
    double? FrequenceMinimaleMhz = null,
    /// <summary>L'union des raisons vues pendant la passe. Un plafond qui n'a dure qu'un instant
    /// a quand meme eu lieu, et une intersection l'effacerait.</summary>
    ClocksEventReason? RaisonsVues = null)
{
    public static readonly HardwareObservation Vide = new(0, null, null);

    /// <summary>Agrege des lectures brutes, chaque grandeur a part : une carte peut rendre sa temperature et pas ses
    /// watts (les Ada consumer sur l'appel instantane).</summary>
    public static HardwareObservation De(IReadOnlyList<HardwareReading> lectures)
    {
        if (lectures is null || lectures.Count == 0) return Vide;

        var watts = lectures.Select(l => l.GpuPowerW).OfType<double>()
            .Where(x => x > 0 && double.IsFinite(x)).OrderBy(x => x).ToList();
        var degres = lectures.Select(l => l.GpuTemperatureC).OfType<double>()
            .Where(x => x > 0 && double.IsFinite(x)).ToList();
        var mhz = lectures.Select(l => l.GpuClockMhz).OfType<double>()
            .Where(x => x > 0 && double.IsFinite(x)).OrderBy(x => x).ToList();

        // L'union : un plafond d'un seul echantillon a eu lieu. Aucune lecture aboutie ⇒ null, pas `Aucune` (« rien ne
        // bridait » sans avoir regarde).
        var lues = lectures.Select(l => l.Raisons).OfType<ClocksEventReason>().ToList();
        ClocksEventReason? raisons = lues.Count == 0
            ? null
            : lues.Aggregate(ClocksEventReason.Aucune, (a, b) => a | b);

        return new HardwareObservation(
            lectures.Count,
            watts.Count > 0 ? watts[watts.Count / 2] : null,
            degres.Count > 0 ? degres.Max() : null,
            mhz.Count > 0 ? mhz[mhz.Count / 2] : null,
            mhz.Count > 0 ? mhz[0] : null,
            raisons);
    }
}
