namespace DrDxt.Core;

/// <summary>
/// Un instant de la machine (a ne pas confondre avec `PerformanceSample`, qui est une mesure
/// entiere conservee dans le journal entre deux sessions : ici c'est un instant dans une session), creux, et c'est deliberé.
///
/// Jusqu'ici, DrDXT correlait « la frame 872 avec la valeur 872 » : les series etaient
/// appariees par index, ce qui interdit de fusionner deux producteurs qui n'echantillonnent pas au
/// meme rythme. Le frametime vient de PresentMon a chaque presentation, la telemetrie GPU de NVML
/// toutes les N millisecondes, les DPC/ISR d'ETW par rafales. Trois horloges, trois cadences :
/// aucune ne s'aligne sur un index.
///
/// Chaque champ optionnel vaut <c>null</c> quand il n'a pas ete mesure. **Une timeline creuse avec
/// des trous honnetes vaut mieux qu'une timeline pleine de valeurs inventees** : c'est la meme
/// doctrine que `null = pas mesure, jamais zero` ailleurs dans le produit.
/// </summary>
/// <param name="TimestampNs">L'instant, en nanosecondes depuis une origine commune a tous les
/// producteurs de la session. La valeur absolue n'a aucun sens ; seuls les ecarts en ont.</param>
public sealed record TimelineSample(
    long TimestampNs,
    double? FrameTimeMs = null,
    double? CpuBusyMs = null,
    double? GpuBusyMs = null,
    double? GpuWaitMs = null,
    double? GpuClockMhz = null,
    double? GpuPowerW = null,
    double? GpuTemperatureC = null,
    double? GpuUtilizationPct = null,
    string? ThrottleReason = null,
    double? DpcIsrMs = null);

/// <summary>
/// La base de temps commune : sans elle, aucun producteur ne peut etre fusionne avec un autre.
///
/// Windows expose un compteur haute resolution (QPC) monotone et partage par tous les processus :
/// c'est la seule horloge sur laquelle PresentMon, NVML et ETW peuvent s'accorder. Cette interface
/// est la couture qui permet de tester toute la fusion **sans machine**, et de brancher le vrai
/// QPC en production.
///
/// Bonne nouvelle mesuree le 07/09 : PresentMon 2.5.1 expose `PM_METRIC_SESSION_START_QPC`.
/// Le jour ou la paire vendorée montera de version, les frametimes porteront leur propre QPC et
/// se fusionneront ici sans conversion.
/// </summary>
public interface IMonotonicTimestamps
{
    /// <summary>Nanosecondes depuis l'origine de la session. Doit etre monotone : un producteur
    /// qui recule rendrait toute fusion fausse sans qu'aucune erreur ne soit levee.</summary>
    long NowNs();
}

/// <summary>
/// La fusion : plusieurs producteurs, une seule ligne de temps.
///
/// Ce que ce type permet, et qu'aucune serie parallele ne permettait : demander **« a cet instant
/// precis, que faisait la machine ? »** en rapprochant des mesures qui n'ont ni la meme cadence ni
/// la meme origine.
///
/// Le rapprochement a une tolerance, et elle est explicite. Deux echantillons distants de
/// 500 ms ne decrivent pas le meme instant, meme s'ils sont les plus proches l'un de l'autre. Au
/// dela de la tolerance, le champ reste <c>null</c> : « je n'ai pas regarde a cet instant » plutot
/// qu'une valeur prise ailleurs.
///
/// Pur, aucune I/O, aucune horloge : les instants sont fournis par l'appelant.
/// </summary>
public static class PerformanceTimeline
{
    /// <summary>Ecart maximal, par defaut, pour qu'un echantillon de telemetrie soit considere
    /// comme decrivant le meme instant qu'une frame.
    ///
    /// **Non chiffré** : 20 ms est de l'ordre de trois frames a 144 Hz et du pas d'echantillonnage
    /// typique d'un capteur GPU. Ecrit ici pour etre trouve et discute, pas dilue dans une boucle.</summary>
    public const long ToleranceParDefautNs = 20_000_000;

    /// <summary>
    /// Fusionne une serie de reference (typiquement les frames) avec une serie de telemetrie,
    /// en rapprochant chaque element de reference du plus proche echantillon dans la tolerance.
    ///
    /// Les deux series doivent etre triees par instant. Le parcours est lineaire : une session de
    /// dix minutes a 200 fps fait 120 000 frames, et une recherche par frame serait quadratique.
    /// </summary>
    public static IReadOnlyList<TimelineSample> Fusionner(
        IReadOnlyList<TimelineSample> reference,
        IReadOnlyList<TimelineSample> telemetrie,
        long toleranceNs = ToleranceParDefautNs)
    {
        if (reference is null || reference.Count == 0) return Array.Empty<TimelineSample>();
        if (telemetrie is null || telemetrie.Count == 0) return reference;

        var fusionnes = new List<TimelineSample>(reference.Count);
        var j = 0;

        foreach (var r in reference)
        {
            // Avance tant que l'echantillon suivant est plus proche : le plus proche peut etre
            // avant comme apres, et prendre systematiquement le precedent decalerait tout.
            while (j + 1 < telemetrie.Count
                   && Math.Abs(telemetrie[j + 1].TimestampNs - r.TimestampNs)
                      <= Math.Abs(telemetrie[j].TimestampNs - r.TimestampNs))
                j++;

            var proche = telemetrie[j];
            fusionnes.Add(Math.Abs(proche.TimestampNs - r.TimestampNs) <= toleranceNs
                ? Completer(r, proche)
                : r);   // hors tolerance : on ne remplit rien plutot que de remplir a cote.
        }

        return fusionnes;
    }

    /// <summary>Complete les champs vides de la reference avec ceux de l'echantillon. Ne remplace
    /// jamais une valeur deja mesuree : la reference reste la source de ce qu'elle porte.</summary>
    private static TimelineSample Completer(TimelineSample r, TimelineSample t) => r with
    {
        CpuBusyMs = r.CpuBusyMs ?? t.CpuBusyMs,
        GpuBusyMs = r.GpuBusyMs ?? t.GpuBusyMs,
        GpuWaitMs = r.GpuWaitMs ?? t.GpuWaitMs,
        GpuClockMhz = r.GpuClockMhz ?? t.GpuClockMhz,
        GpuPowerW = r.GpuPowerW ?? t.GpuPowerW,
        GpuTemperatureC = r.GpuTemperatureC ?? t.GpuTemperatureC,
        GpuUtilizationPct = r.GpuUtilizationPct ?? t.GpuUtilizationPct,
        ThrottleReason = r.ThrottleReason ?? t.ThrottleReason,
        DpcIsrMs = r.DpcIsrMs ?? t.DpcIsrMs,
    };

    /// <summary>Quelle part des elements de reference a pu etre completee ? Dans [0, 1].
    ///
    /// C'est l'honnetete de la timeline, et elle doit etre affichee : une correlation calculee sur
    /// 4 % des frames ne vaut rien, et rien d'autre ne le dirait.</summary>
    public static double Couverture(IReadOnlyList<TimelineSample> fusionnes, Func<TimelineSample, double?> champ)
    {
        if (fusionnes is null || fusionnes.Count == 0) return 0;
        return (double)fusionnes.Count(s => champ(s).HasValue) / fusionnes.Count;
    }
}
