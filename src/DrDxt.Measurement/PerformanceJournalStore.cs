using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>La frontiere disque du journal. Une seule chaine traverse, pour que le magasin se teste
/// sans fichier : meme forme que <c>IMemoryBaselineFile</c>, volontairement.</summary>
public interface IPerformanceJournalFile
{
    Result WriteAtomic(string json);
    Result<string?> Read();
}

/// <summary>
/// Le journal des mesures, pour repondre a « mon PC est-il plus lent qu'avant ? » : une degradation lente, aucun scan
/// isole ne la voit. Le magasin ajoute, relit, elague ; le jugement vit dans <see cref="PerformanceJournal"/>, pur. Une
/// lecture ratee rend un journal vide (pas d'historique est normal, jamais un scan en echec) ; une ecriture ratee se dit.
/// </summary>
public sealed class PerformanceJournalStore
{
    /// <summary>Plusieurs mois de scans occasionnels, lisible a la main. L'elagage garde toujours la mesure la plus
    /// ancienne (<see cref="PerformanceJournal.Elaguer"/>).</summary>
    public const int MaximumEntrees = 200;

    // La marge vaut +infini sous trois passes, que le JSON par defaut refuse d'ecrire.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly IPerformanceJournalFile _fichier;

    public PerformanceJournalStore(IPerformanceJournalFile fichier) => _fichier = fichier;

    /// <summary>Tout l'historique lisible ; vide sans historique ou fichier illisible.</summary>
    public IReadOnlyList<PerformanceSample> Lire()
        => LireOuEchec() is { IsOk: true } lu ? lu.Value! : Array.Empty<PerformanceSample>();

    /// <summary>La lecture qui dit son echec. Absent ou vide = journal vide ; lecture ratee ou JSON
    /// corrompu ne le sont pas.</summary>
    private Result<IReadOnlyList<PerformanceSample>> LireOuEchec()
    {
        var brut = _fichier.Read();
        if (!brut.IsOk)
            return Result<IReadOnlyList<PerformanceSample>>.Fail(brut.Error!.Value.Code, brut.Error.Value.Message);
        if (string.IsNullOrWhiteSpace(brut.Value))
            return Result<IReadOnlyList<PerformanceSample>>.Ok(Array.Empty<PerformanceSample>());

        try
        {
            var entrees = JsonSerializer.Deserialize<List<PerformanceSample>>(brut.Value!, Options);
            // Sans machine ou sans FPS, une entree ne sert ni de reference ni de mesure.
            return Result<IReadOnlyList<PerformanceSample>>.Ok(
                entrees?.Where(e => e is not null
                                 && !string.IsNullOrWhiteSpace(e.MachineId)
                                 && e.AvgFps > 0
                                 // Relue sans marge, une mesure vaudrait 0 : la plus « precise »,
                                 // donc l'etalon, avec une resolution de derive fausse.
                                 && e.MarginOfError > 0).ToList()
                ?? (IReadOnlyList<PerformanceSample>)Array.Empty<PerformanceSample>());
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<PerformanceSample>>.Fail(ErrorCodes.CaptureFail,
                "Journal de performance illisible : " + ex.Message);
        }
    }

    /// <summary>Ajoute une mesure et rend le journal ecrit. Sans lecture sure, on n'ecrit pas : la plus ancienne mesure,
    /// reference de toute derive, disparaitrait.</summary>
    public Result<IReadOnlyList<PerformanceSample>> Ajouter(PerformanceSample mesure)
    {
        if (mesure is null || string.IsNullOrWhiteSpace(mesure.MachineId) || !(mesure.AvgFps > 0))
            return Result<IReadOnlyList<PerformanceSample>>.Fail(
                ErrorCodes.CaptureFail, Docteur.T("PJS.Inexploitable"));

        var existant = LireOuEchec();
        if (!existant.IsOk)
            return Result<IReadOnlyList<PerformanceSample>>.Fail(existant.Error!.Value.Code,
                Docteur.T("PJS.NonJournalisee", existant.Error.Value.Message));

        var journal = PerformanceJournal.Elaguer(
            existant.Value!.Append(mesure).ToList(), MaximumEntrees);

        var ecriture = _fichier.WriteAtomic(JsonSerializer.Serialize(journal, Options));
        return ecriture.IsOk
            ? Result<IReadOnlyList<PerformanceSample>>.Ok(journal)
            : Result<IReadOnlyList<PerformanceSample>>.Fail(
                ecriture.Error!.Value.Code, ecriture.Error.Value.Message);
    }
}

/// <summary>
/// Le fichier reel, ecrit atomiquement (temporaire sur le meme volume, puis renommage) : a moitie ecrit, il se
/// comparerait quand meme. Meme forme que <c>FileSystemMemoryBaselineFile</c>.
/// </summary>
public sealed class FileSystemPerformanceJournalFile : IPerformanceJournalFile
{
    private readonly string _path;

    public static string DefaultPath()
        => DrDxt.Core.DossierDonnees.Chemin("performance-journal.json");

    public FileSystemPerformanceJournalFile() : this(DefaultPath()) { }

    public FileSystemPerformanceJournalFile(string path)
        => _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A path is required.", nameof(path))
            : path;

    // L'ecriture atomique a une seule implementation (Core).
    private FichierJsonAtomique Fichier => new(_path, ErrorCodes.CaptureFail);

    public Result WriteAtomic(string json) => Fichier.WriteAtomic(json);

    public Result<string?> Read() => Fichier.Read();
}
