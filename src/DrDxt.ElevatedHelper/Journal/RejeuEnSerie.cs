using System.Text;
using System.Text.Json;
using DrDxt.Core;
using DrDxt.Reconciler.Desinstallation;

namespace DrDxt.ElevatedHelper.Journal;

/// <summary>
/// Les rejeux d'un hôte, un à la fois. Au démarrage, puis chaque fois qu'un client s'authentifie : l'App relancée après un
/// plantage se connecte au helper resté vivant, et ce que l'App morte a laissé est rendu sans attendre le prochain démarrage.
/// La garde « demandeur mort » protège l'App qui vient de se connecter. Une demande arrivée pendant un rejeu en lance un
/// seul autre après lui ; une rafale de connexions n'en empile pas. Un rejeu qui échoue ne lève jamais.
/// </summary>
public sealed class RejeuEnSerie(Func<CancellationToken, Task<RapportDuRendu?>> rejouer, Action<RapportDuRendu> rapporter, TimeSpan borne)
{
    private readonly SemaphoreSlim _un = new(1, 1);
    private int _enFile;

    public async Task RejouerAsync(CancellationToken ct)
    {
        await _un.WaitAsync(ct).ConfigureAwait(false);
        try { await UnRejeuAsync(ct).ConfigureAwait(false); }
        finally { _un.Release(); }
    }

    /// <summary>En tâche de fond, sans attendre. La tâche rendue sert aux tests.</summary>
    public Task Demander()
    {
        if (Interlocked.Exchange(ref _enFile, 1) == 1) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            await _un.WaitAsync().ConfigureAwait(false);
            // Libérée dès le début : une demande arrivée pendant ce rejeu en lance un après lui.
            Volatile.Write(ref _enFile, 0);
            try { await UnRejeuAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { _un.Release(); }
        });
    }

    /// <summary>Le rejeu du helper (son démarrage, une connexion) : différé tant qu'un jeu connu tourne (bcdedit, NVAPI,
    /// écritures en pleine partie), repris à la connexion suivante hors jeu. Celui du service au démarrage de Windows passe
    /// toujours : aucune partie n'est encore lancée.</summary>
    public Task DemanderHorsJeu(Func<bool> jeuEnCours) => jeuEnCours() ? Task.CompletedTask : Demander();

    /// <summary>Un jeu connu par son nom de processus tourne-t-il ? Une liste illisible ne retient rien.</summary>
    public static bool JeuConnuLance()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
                using (p)
                    if (NomExeDeJeu.EstUnJeuConnu(p.ProcessName + ".exe")) return true;
        }
        catch (Exception) { /* liste illisible : le rejeu passe, comme avant cette garde */ }
        return false;
    }

    private async Task UnRejeuAsync(CancellationToken ct)
    {
        try
        {
            using var b = CancellationTokenSource.CreateLinkedTokenSource(ct);
            b.CancelAfter(borne);
            if (await rejouer(b.Token).ConfigureAwait(false) is { } r && (r.Lignes.Count > 0 || r.ManifesteNonLu is not null))
                rapporter(r);
        }
        catch (Exception)
        {
            // Le suivant retente : les entrées non rendues restent au journal.
        }
    }
}

/// <summary>Les rapports de rejeu, écrits à la racine (lisibles par l'App, écrits par les seuls hôtes élevés). Un fichier par
/// écrivain (le helper, le service) qui garde ses derniers rapports : le rejeu du service au démarrage de Windows, puis celui
/// du helper à la connexion de l'App, se lisent tous les deux, aucun n'efface l'autre. L'App dit une fois ceux plus récents
/// que le dernier dit.</summary>
public sealed record RapportDuRejeu(DateTimeOffset Le, string? NonLu, IReadOnlyList<RapportDuRejeu.Ligne> Lignes)
{
    public const string Helper = "helper", Service = "service";
    public const int Gardes = 20;

    public static string Fichier(string ecrivain) => $"rejeu-du-journal.{ecrivain}.json";

    public sealed record Ligne(string Cible, IssueDuRendu Issue, string Detail, bool EnSuspens);

    public static RapportDuRejeu De(RapportDuRendu r, DateTimeOffset le)
        => new(le, r.ManifesteNonLu, r.Lignes.Select(l => new Ligne(l.Cible, l.Issue, l.Detail, l.OrigineEnSuspens)).ToList());

    /// <summary>Ajoute le rapport aux derniers de cet écrivain. Un rapport qui n'a rien jugé (tout en suspens : la ruche du
    /// joueur vue du service à chaque démarrage) n'est pas ajouté.</summary>
    public static Result Ecrire(string racine, string ecrivain, RapportDuRendu r, DateTimeOffset le, Func<string, byte[], Result> ecrire)
    {
        if (r.ManifesteNonLu is null && r.Lignes.All(l => l.OrigineEnSuspens)) return Result.Ok();
        var chemin = Path.Combine(racine, Fichier(ecrivain));
        var tous = LireUn(chemin).Append(De(r, le)).TakeLast(Gardes).ToList();
        return ecrire(chemin, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tous)));
    }

    /// <summary>Les rapports des deux écrivains, du plus ancien au plus récent. Un fichier absent ou illisible n'en donne aucun.</summary>
    public static IReadOnlyList<RapportDuRejeu> Lire(string racine)
        => LireUn(Path.Combine(racine, Fichier(Helper))).Concat(LireUn(Path.Combine(racine, Fichier(Service))))
            .OrderBy(r => r.Le).ToList();

    // Illisible, il est lu vide puis réécrit avec le seul nouveau rapport : d'anciens rapports non dits se perdent, jamais une
    // origine (le fichier n'est écrit que par un hôte élevé).
    private static IReadOnlyList<RapportDuRejeu> LireUn(string chemin)
    {
        try
        {
            return File.Exists(chemin) && JsonSerializer.Deserialize<List<RapportDuRejeu>>(File.ReadAllText(chemin)) is { } l
                ? l.Where(r => r is { Lignes: not null }).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
