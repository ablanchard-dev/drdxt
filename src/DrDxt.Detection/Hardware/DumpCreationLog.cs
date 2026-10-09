using System.Diagnostics.Eventing.Reader;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Ce que Windows a garde apres ses plantages, et combien de fois il n'a rien pu garder.</summary>
public sealed record EtatDesVidages(IReadOnlyList<DateTimeOffset> Echecs, int PetitsVidages, bool VidageComplet,
    string DernierMessage = "");

/// <summary>
/// Les echecs de creation de vidage (`volmgr` 161) et les vidages presents.
///
/// 17/09/2026 : l'outil de developpement lisait cet evenement depuis des jours, le produit non. Sur
/// la machine de reference : 10 arrets brutaux, 2 echecs de creation (02/09 et 04/09), aucun vidage
/// garde. L'app disait comment les vidages sont configures, jamais qu'ils ne s'ecrivent pas.
/// La difference entre « configure pour » et « capable de ».
/// </summary>
public interface IDumpCreationLog
{
    Result<EtatDesVidages> Lire(DateTimeOffset depuis);
}

public sealed class WindowsDumpCreationLog : IDumpCreationLog
{
    private const int MaxEvenements = 50;

    public Result<EtatDesVidages> Lire(DateTimeOffset depuis)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='volmgr'] and (EventID=161)]]") { ReverseDirection = true };

            var echecs = new List<DateTimeOffset>();
            var dernierMessage = "";
            using (var reader = new EventLogReader(query))
                for (EventRecord? e = reader.ReadEvent(); e is not null && echecs.Count < MaxEvenements; e = reader.ReadEvent())
                    using (e)
                    {
                        if (e.TimeCreated is not { } t || new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) < depuis) continue;
                        echecs.Add(new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero));
                        // Lecture a rebours : le premier message lu est celui du dernier echec.
                        if (dernierMessage.Length == 0)
                            try { dernierMessage = (e.FormatDescription() ?? "").Trim(); }
                            catch (EventLogException) { }
                    }
            echecs.Sort();

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var minidump = Path.Combine(windows, "Minidump");
            var petits = Directory.Exists(minidump) ? Directory.GetFiles(minidump, "*.dmp").Length : 0;
            var complet = File.Exists(Path.Combine(windows, "MEMORY.DMP"));

            return Result<EtatDesVidages>.Ok(new EtatDesVidages(echecs, petits, complet, dernierMessage));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or IOException)
        {
            return Result<EtatDesVidages>.Fail(ErrorCodes.DetectionFailed, ex.Message);
        }
    }
}
