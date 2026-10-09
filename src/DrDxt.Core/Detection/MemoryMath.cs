namespace DrDxt.Core;

/// <summary>
/// L'arithmétique de la mémoire — fonctions pures, aucune I/O, aucune supposition matérielle.
///
/// Pourquoi ça existe : la fréquence seule ne dit rien de la latence. Un kit DDR5-6000 CL30 et
/// un DDR4-3200 CL16 ont exactement la même latence vraie (10,00 ns) malgré 88 % d'écart de
/// fréquence affichée. Comparer deux configurations sur les MT/s est le raccourci qui fait
/// acheter la mauvaise barrette.
///
/// Ce que ces fonctions ne font pas : elles ne mesurent rien. Elles convertissent des
/// grandeurs déjà obtenues ailleurs. La latence calculée vaut ce que vaut le tCL fourni — et
/// le tCL vient de la référence du kit (part number), pas de la machine, parce qu'aucune API
/// Windows documentée n'expose les timings effectifs. Si la carte a changé le tCL, le chiffre
/// est faux, et l'appelant doit le présenter comme « la latence du kit à la fréquence mesurée »,
/// jamais comme « ta latence actuelle ».
/// </summary>
public static class MemoryMath
{
    /// <summary>
    /// Durée d'un cycle mémoire, en nanosecondes, depuis le débit en MT/s.
    ///
    /// DDR = Double Data Rate : deux transferts par cycle, donc l'horloge réelle vaut la moitié
    /// du chiffre commercial. DDR4-3800 = 1900 MHz réels ⇒ tCK = 1/1900 MHz = 0,5263 ns.
    /// D'où la forme compacte : tCK(ns) = 2000 / MT/s.
    /// </summary>
    public static double CycleTimeNs(int dataRateMts)
        => dataRateMts <= 0 ? 0 : 2000.0 / dataRateMts;

    /// <summary>
    /// Latence vraie en nanosecondes : <c>tCL × tCK</c>.
    ///
    /// C'est la seule grandeur comparable entre deux kits. Vérifiée contre trois repères
    /// publics dans les tests : DDR4-3200 CL16 = 10,00 ns · DDR4-3600 CL16 = 8,89 ns ·
    /// DDR5-6000 CL30 = 10,00 ns.
    /// </summary>
    public static double TrueLatencyNs(int casLatency, int dataRateMts)
        => casLatency <= 0 || dataRateMts <= 0 ? 0 : casLatency * CycleTimeNs(dataRateMts);
}
