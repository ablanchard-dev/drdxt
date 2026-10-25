using System.Diagnostics.Eventing.Reader;
using System.Management;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Les analyses de Microsoft Defender, lues sans droits dans
/// « Microsoft-Windows-Windows Defender/Operational » (1000 début, 1001 fin, 1002 arrêtée avant la fin), reliées par
/// l'identifiant d'analyse (troisième champ de l'événement). Et le réglage ScanOnlyIfIdleEnabled de MSFT_MpPreference.
/// Lecture seule ; <c>null</c> = non lu, jamais « aucune ».
/// Mesure du 07/10 sur la machine de référence : 62 événements en 30 jours, analyses rapides de 1 à 2 minutes, plusieurs
/// arrêtées avant la fin, ScanOnlyIfIdleEnabled = vrai.
/// </summary>
public static class JournalDefender
{
    public const string Journal = "Microsoft-Windows-Windows Defender/Operational";

    public static IReadOnlyList<AnalyseDefender>? Analyses(DateTimeOffset earliest)
    {
        try
        {
            var ms = (long)Math.Max(0, (DateTimeOffset.UtcNow - earliest).TotalMilliseconds);
            var query = new EventLogQuery(Journal, PathType.LogName,
                $"*[System[(EventID=1000 or EventID=1001 or EventID=1002) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            var evenements = new List<(int Id, DateTimeOffset Quand, string? Analyse)>();
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                    if (e.TimeCreated is { } t)
                        evenements.Add((e.Id, new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero),
                            e.Properties.Count > 2 ? e.Properties[2].Value?.ToString() : null));
            return Relier(evenements);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Un début (1000) et sa fin (1001 ou 1002) de même identifiant forment une analyse ; un début sans fin lue
    /// reste une analyse sans fin ; une fin sans début (hors fenêtre) est ignorée.</summary>
    internal static IReadOnlyList<AnalyseDefender> Relier(IEnumerable<(int Id, DateTimeOffset Quand, string? Analyse)> evenements)
    {
        var liste = evenements.ToList();
        return liste.Where(e => e.Id == 1000 && e.Analyse is not null)
            .Select(d =>
            {
                var fin = liste.Where(f => f.Id is 1001 or 1002 && f.Analyse == d.Analyse && f.Quand >= d.Quand)
                    .OrderBy(f => f.Quand).Select(f => ((DateTimeOffset Quand, bool Arretee)?)(f.Quand, f.Id == 1002)).FirstOrDefault();
                return new AnalyseDefender(d.Quand, fin?.Quand, fin?.Arretee == true);
            })
            .OrderBy(a => a.Debut).ToList();
    }

    /// <summary>ScanOnlyIfIdleEnabled, ou <c>null</c> si non lu.</summary>
    public static bool? SeulementInactif() => LectureWmiBornee.Lire<bool?>(() =>
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT ScanOnlyIfIdleEnabled FROM MSFT_MpPreference");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<ManagementObject>())
                using (o)
                    if (o["ScanOnlyIfIdleEnabled"] is bool b) return b;
            return null;
        }
        catch (Exception) { return null; }
    }, () => null);
}
