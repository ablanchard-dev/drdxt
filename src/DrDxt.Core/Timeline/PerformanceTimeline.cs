namespace DrDxt.Core;

/// <summary>
/// Un instant de la machine dans une session (pas `PerformanceSample`, une mesure entiere gardee entre sessions), creux
/// par choix. Les producteurs n'echantillonnent pas au meme rythme (PresentMon a chaque presentation, NVML toutes les
/// N ms, ETW par rafales) : aucun ne s'aligne sur un index. Un champ optionnel vaut <c>null</c> quand il n'a pas ete
/// mesure : **des trous honnetes plutot que des valeurs inventees**.
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
/// La base de temps commune. Le compteur haute resolution de Windows (QPC), monotone et partage par tous les processus,
/// est la seule horloge commune a PresentMon, NVML et ETW ; cette couture teste la fusion **sans machine** et branche
/// le vrai QPC en production. Mesure du 07/09 : PresentMon 2.5.1 expose `PM_METRIC_SESSION_START_QPC`, les frametimes
/// de cette version porteraient leur propre QPC.
/// </summary>
public interface IMonotonicTimestamps
{
    /// <summary>Nanosecondes depuis l'origine de la session. Doit etre monotone : un producteur
    /// qui recule rendrait toute fusion fausse sans qu'aucune erreur ne soit levee.</summary>
    long NowNs();
}

/// <summary>
/// La fusion : plusieurs producteurs, une seule ligne de temps, pour demander **« a cet instant, que faisait la
/// machine ? »**. Le rapprochement a une tolerance explicite (deux echantillons a 500 ms ne decrivent pas le meme
/// instant) ; au-dela, le champ reste <c>null</c>. Pur : aucune I/O, aucune horloge, instants fournis par l'appelant.
/// </summary>
public static class PerformanceTimeline
{
    /// <summary>Ecart maximal par defaut pour qu'un echantillon de telemetrie decrive le meme instant qu'une frame.
    /// **Non chiffré** : 20 ms, environ trois frames a 144 Hz et le pas typique d'un capteur GPU.</summary>
    public const long ToleranceParDefautNs = 20_000_000;

    /// <summary>
    /// Fusionne une serie de reference (typiquement les frames) avec une serie de telemetrie : chaque element au plus proche
    /// echantillon dans la tolerance. Series triees par instant ; parcours lineaire (dix minutes a 200 fps = 120 000 frames).
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

    /// <summary>Quelle part des elements de reference a pu etre completee, dans [0, 1] : a afficher, une correlation sur
    /// 4 % des frames ne vaut rien.</summary>
    public static double Couverture(IReadOnlyList<TimelineSample> fusionnes, Func<TimelineSample, double?> champ)
    {
        if (fusionnes is null || fusionnes.Count == 0) return 0;
        return (double)fusionnes.Count(s => champ(s).HasValue) / fusionnes.Count;
    }
}
