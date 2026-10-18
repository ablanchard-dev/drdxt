using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Latency;

/// <summary>
/// Quel cœur reçoit les interruptions et les DPC des pilotes, et combien de temps ils lui prennent, sur tous les groupes
/// de processeurs. Un cœur qui passe une grosse part de son temps à servir les pilotes en a moins pour le jeu. Deux
/// relevés espacés d'une seconde ; lecture seule, rien n'est épinglé ni déplacé.
/// </summary>
/// <remarks>
/// Compteurs « Processor Information » de Windows, une instance par processeur logique nommée « groupe,numéro »
/// (Win32_PerfRawData_Counters_ProcessorInformation) : « Interrupts/sec » et « DPCs Queued/sec » sont des compteurs
/// de type PERF_COUNTER_COUNTER (delta sur delta de Timestamp_PerfTime / Frequency_PerfTime), « % DPC Time » et
/// « % Interrupt Time » des PERF_100NSEC_TIMER (delta sur delta de Timestamp_Sys100NS) ; types relevés dans les
/// qualificatifs de la classe sur la machine de référence le 16/10/2026, définis par Microsoft Learn (« Counter Types »).
/// </remarks>
public static class RecensementParCoeur
{
    public sealed record Brut(ulong Interruptions, ulong Dpc, ulong TempsDpc, ulong TempsInterruptions, ulong Sys100Ns, ulong PerfTime, ulong PerfFreq);

    /// <param name="Part">Part du temps du cœur passée à servir DPC et interruptions, de 0 à 1.</param>
    public sealed record Coeur(string Nom, double InterruptionsParSeconde, double DpcParSeconde, double Part);

    /// <summary>Un cœur dont la part dépasse ce seuil, et trois fois la médiane des autres, est dit confisqué. **Non chiffré** :
    /// un quart de son temps, c'est un cœur que le jeu ne peut plus compter comme entier.</summary>
    public const double SeuilConfisque = 0.25;

    public static IReadOnlyDictionary<string, Brut>? Lire()
    {
        try
        {
            var lus = new Dictionary<string, Brut>(StringComparer.Ordinal);
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT Name, InterruptsPersec, DPCsQueuedPersec, PercentDPCTime, PercentInterruptTime, Timestamp_Sys100NS, "
                + "Timestamp_PerfTime, Frequency_PerfTime FROM Win32_PerfRawData_Counters_ProcessorInformation");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                {
                    if (o["Name"] is not string nom || nom.Contains("_Total", StringComparison.Ordinal)) continue;
                    ulong U(string n) => Convert.ToUInt64(o[n], CultureInfo.InvariantCulture);
                    lus[nom] = new Brut(U("InterruptsPersec"), U("DPCsQueuedPersec"), U("PercentDPCTime"), U("PercentInterruptTime"),
                        U("Timestamp_Sys100NS"), U("Timestamp_PerfTime"), U("Frequency_PerfTime"));
                }
            return lus.Count > 0 ? lus : null;
        }
        catch (Exception) { return null; }
    }

    public static IReadOnlyList<Coeur>? Mesurer(TimeSpan ecart)
    {
        if (Lire() is not { } a) return null;
        Thread.Sleep(ecart);
        return Lire() is { } b ? Calculer(a, b) : null;
    }

    /// <summary>Pur : par cœur présent dans les deux relevés ; un compteur qui recule rend le cœur illisible.</summary>
    public static IReadOnlyList<Coeur> Calculer(IReadOnlyDictionary<string, Brut> a, IReadOnlyDictionary<string, Brut> b)
    {
        var coeurs = new List<Coeur>();
        foreach (var (nom, y) in b)
        {
            if (!a.TryGetValue(nom, out var x) || y.Sys100Ns <= x.Sys100Ns || y.PerfTime <= x.PerfTime || x.PerfFreq == 0
                || y.Interruptions < x.Interruptions || y.Dpc < x.Dpc || y.TempsDpc < x.TempsDpc || y.TempsInterruptions < x.TempsInterruptions)
                continue;
            var secondes = (double)(y.PerfTime - x.PerfTime) / x.PerfFreq;
            double dt = y.Sys100Ns - x.Sys100Ns;
            coeurs.Add(new Coeur(nom, (y.Interruptions - x.Interruptions) / secondes, (y.Dpc - x.Dpc) / secondes,
                Math.Clamp((y.TempsDpc - x.TempsDpc + (double)(y.TempsInterruptions - x.TempsInterruptions)) / dt, 0, 1)));
        }
        return coeurs.OrderBy(c => c.Nom, StringComparer.Ordinal).ToList();
    }

    /// <summary>Pur : le cœur le plus chargé, nommé avec ses chiffres ; confisqué au-delà du seuil.</summary>
    public static RepairFinding Juger(IReadOnlyList<Coeur>? coeurs)
    {
        if (coeurs is null || coeurs.Count == 0)
            return new RepairFinding("LATENCE", "recensement par coeur", null, null, RepairStatus.Unknown, Docteur.T("RP.NonLu"));
        var max = coeurs.MaxBy(c => c.Part)!;
        var autres = coeurs.Where(c => c != max).Select(c => c.Part).Order().ToList();
        var mediane = autres.Count == 0 ? 0 : autres[autres.Count / 2];
        var valeur = Docteur.T("RP.Valeur", max.Nom, max.Part * 100);
        var phrase = Docteur.T("RP.Plus", max.Nom, max.InterruptionsParSeconde, max.DpcParSeconde, max.Part * 100, mediane * 100, coeurs.Count);
        return max.Part >= SeuilConfisque && max.Part >= 3 * mediane
            ? new RepairFinding("LATENCE", "recensement par coeur", valeur, null, RepairStatus.Suspicious, phrase + Docteur.T("RP.Confisque"))
            : new RepairFinding("LATENCE", "recensement par coeur", valeur, null, RepairStatus.Observed, phrase);
    }
}
