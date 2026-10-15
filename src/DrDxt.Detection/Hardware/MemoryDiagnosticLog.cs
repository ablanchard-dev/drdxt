using System.Diagnostics.Eventing.Reader;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Un passage du Diagnostic de memoire Windows, tel que Windows l'a note.</summary>
public sealed record ResultatTestMemoire(DateTimeOffset Quand, int EventId, string Message);

/// <summary>
/// Ce que le diagnostic de memoire Windows a ecrit dans le journal.
///
/// Le produit ne traduit pas le numero d'evenement en verdict : deux familles de
/// sources se contredisent (1101/1102 contre 1201/1202 pour « rien trouve » / « erreurs »). Il rend
/// le message que Windows a lui-meme ecrit, localise, et laisse le joueur le lire.
/// Une liste vide est une reponse (« jamais lance ») ; un journal illisible est un `Fail`.
/// </summary>
public interface IMemoryDiagnosticLog
{
    Result<IReadOnlyList<ResultatTestMemoire>> Lire();
}

public sealed class WindowsMemoryDiagnosticLog : IMemoryDiagnosticLog
{
    private const string Fournisseur = "Microsoft-Windows-MemoryDiagnostics-Results";
    private const int MaxEvenements = 10;

    public Result<IReadOnlyList<ResultatTestMemoire>> Lire()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{Fournisseur}']]]") { ReverseDirection = true };

            var lus = new List<ResultatTestMemoire>();
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e is not null && lus.Count < MaxEvenements; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated is not { } t) continue;
                    string message;
                    try { message = e.FormatDescription() ?? ""; }
                    catch (EventLogException) { message = ""; }
                    lus.Add(new ResultatTestMemoire(new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero), e.Id, message.Trim()));
                }
            }

            return Result<IReadOnlyList<ResultatTestMemoire>>.Ok(lus);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<ResultatTestMemoire>>.Fail(ErrorCodes.DetectionFailed, ex.Message);
        }
    }
}
