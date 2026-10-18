using System.Diagnostics.Eventing.Reader;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Ce que Windows a garde apres ses plantages dans la fenetre lue, et combien de fois il n'a rien pu garder.</summary>
public sealed record EtatDesVidages(IReadOnlyList<DateTimeOffset> Echecs, int PetitsVidages, bool VidageComplet,
    string DernierMessage = "", DateTimeOffset? DernierVidage = null);

/// <summary>
/// Les echecs de creation de vidage (`volmgr` 161) et les vidages presents.
///
/// Releve sur la machine de reference : 10 arrets brutaux, 2 echecs de creation, aucun vidage garde.
/// Dire comment les vidages sont configures ne dit pas qu'ils s'ecrivent : la difference entre
/// « configure pour » et « capable de ».
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

            var (petits, complet, dernier) = VidagesGardes(Environment.GetFolderPath(Environment.SpecialFolder.Windows), depuis);
            return Result<EtatDesVidages>.Ok(new EtatDesVidages(echecs, petits, complet, dernierMessage, dernier));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or IOException)
        {
            return Result<EtatDesVidages>.Fail(ErrorCodes.DetectionFailed, ex.Message);
        }
    }

    /// <summary>Les vidages ecrits depuis <paramref name="depuis"/> (date de derniere ecriture du fichier) : un vidage
    /// vieux de six mois ne dit rien des plantages de la fenetre, et ne doit pas masquer ses echecs d'ecriture.</summary>
    internal static (int Petits, bool Complet, DateTimeOffset? Dernier) VidagesGardes(string windows, DateTimeOffset depuis)
    {
        var minidump = Path.Combine(windows, "Minidump");
        var petits = Directory.Exists(minidump)
            ? new DirectoryInfo(minidump).GetFiles("*.dmp").Select(f => new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)).Where(d => d >= depuis).ToList()
            : [];
        var memory = new FileInfo(Path.Combine(windows, "MEMORY.DMP"));
        DateTimeOffset? complet = memory.Exists && new DateTimeOffset(memory.LastWriteTimeUtc, TimeSpan.Zero) is var d && d >= depuis ? d : null;
        DateTimeOffset? dernier = petits.Count > 0 ? petits.Max() : null;
        if (complet > dernier || dernier is null) dernier = complet;
        return (petits.Count, complet is not null, dernier);
    }
}

/// <summary>
/// Le journal des vidages lu une fois par fenêtre pour tout un scan. Une fenêtre ne se tire pas d'une plus large : le
/// compte des vidages et le dernier message dépendent de la fenêtre. Les familles qui partagent une fenêtre la
/// demandent depuis le même instant, et ne lisent donc qu'une fois. Un nouveau scan crée un nouveau journal.
/// </summary>
public sealed class VidagesDuScan(IDumpCreationLog source) : IDumpCreationLog
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DateTimeOffset, Lazy<Result<EtatDesVidages>>> _lus = new();

    public Result<EtatDesVidages> Lire(DateTimeOffset depuis)
        => _lus.GetOrAdd(depuis, d => new Lazy<Result<EtatDesVidages>>(() => source.Lire(d))).Value;
}
