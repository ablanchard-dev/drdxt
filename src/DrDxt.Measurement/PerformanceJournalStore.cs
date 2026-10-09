using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>La frontiere disque du journal. Une seule chaine traverse, pour que le magasin se teste
/// sans fichier — meme forme que <c>IMemoryBaselineFile</c>, volontairement.</summary>
public interface IPerformanceJournalFile
{
    Result WriteAtomic(string json);
    Result<string?> Read();
}

/// <summary>
/// Le journal des mesures, et la seule chose qu'il sert a faire.
///
/// Avant lui, un seul fichier survivait a une session : `memory-baseline.json`. Ni les mesures,
/// ni les verdicts, ni les resolutions. Le produit redecouvrait la machine a chaque lancement, donc
/// il ne pouvait pas repondre a « mon PC est-il plus lent qu'avant ? » — et une **degradation
/// lente** est exactement ce qu'aucun scan isole ne voit : chaque mesure prise seule parait normale.
///
/// Le magasin ne fait que trois choses : ajouter, relire, elaguer. Le jugement vit dans
/// <see cref="PerformanceJournal"/>, pur et teste sans disque.
///
/// Une lecture qui echoue rend un journal vide, pas une erreur : ne pas avoir d'historique est
/// un etat normal (premier lancement), et il ne doit jamais faire echouer un scan. Une ecriture qui
/// echoue, elle, se dit — sinon le produit croirait accumuler un historique qui n'existe pas.
/// </summary>
public sealed class PerformanceJournalStore
{
    /// <summary>Nombre d'entrees conservees. Assez pour plusieurs mois de scans occasionnels, assez
    /// peu pour que le fichier reste lisible a la main — c'est un journal de diagnostic, pas une
    /// base de donnees. L'elagage garde toujours la mesure la plus ancienne : voir
    /// <see cref="PerformanceJournal.Elaguer"/>.</summary>
    public const int MaximumEntrees = 200;

    // 26/09/2026 : la marge vaut +infini sous trois passes, et le JSON par defaut refuse d'ecrire l'infini.
    // L'exception etait avalee par le scan : la mesure du jour disparaissait en silence.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly IPerformanceJournalFile _fichier;

    public PerformanceJournalStore(IPerformanceJournalFile fichier) => _fichier = fichier;

    /// <summary>Tout l'historique lisible. Vide quand il n'y en a pas — ou quand le fichier est
    /// illisible : un historique perdu ne doit pas empecher de mesurer aujourd'hui.</summary>
    public IReadOnlyList<PerformanceSample> Lire()
        => LireOuEchec() is { IsOk: true } lu ? lu.Value! : Array.Empty<PerformanceSample>();

    /// <summary>La lecture qui DIT son echec. Absent ou vide = journal vide ; lecture ratee ou JSON
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
            // Une entree sans machine ou sans FPS ne peut servir ni de reference ni de mesure :
            // la garder ferait porter la comparaison sur du vide.
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

    /// <summary>Ajoute une mesure et rend le journal tel qu'il vient d'etre ecrit.
    ///
    /// Corrige LE 10/09/2026 (L2, plan du 10/09). Une lecture ratee ou un JSON corrompu
    /// rendait un journal vide, puis cette methode ecrasait le fichier avec la seule mesure du
    /// jour : la plus ancienne mesure — la REFERENCE de toute derive — disparaissait en silence.
    /// Sans lecture sure, on n'ecrit pas.</summary>
    public Result<IReadOnlyList<PerformanceSample>> Ajouter(PerformanceSample mesure)
    {
        if (mesure is null || string.IsNullOrWhiteSpace(mesure.MachineId) || !(mesure.AvgFps > 0))
            return Result<IReadOnlyList<PerformanceSample>>.Fail(
                ErrorCodes.CaptureFail, "Mesure inexploitable : rien à journaliser.");

        var existant = LireOuEchec();
        if (!existant.IsOk)
            return Result<IReadOnlyList<PerformanceSample>>.Fail(existant.Error!.Value.Code,
                "Mesure non journalisée, pour ne pas écraser un journal que DrDXT n'a pas su lire : "
                + existant.Error.Value.Message);

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
/// Le fichier reel, ecrit ATOMIQUEMENT : temporaire a cote de la cible (meme volume, donc le
/// deplacement est un vrai renommage) puis remplacement. Un journal a moitie ecrit serait pire
/// qu'absent — il se comparerait quand meme.
///
/// Meme forme que <c>FileSystemMemoryBaselineFile</c>, volontairement : deux magasins qui se
/// ressemblent se relisent ensemble.
/// </summary>
public sealed class FileSystemPerformanceJournalFile : IPerformanceJournalFile
{
    private readonly string _path;

    public static string DefaultPath()
        => DrDxt.Core.DossierDonnees.Chemin("performance-journal.json");

    public FileSystemPerformanceJournalFile() : this(DefaultPath()) { }

    public FileSystemPerformanceJournalFile(string path)
        => _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Un chemin est requis.", nameof(path))
            : path;

    // L'ecriture atomique a une seule implementation (Core) : elle etait recopiee ici.
    private FichierJsonAtomique Fichier => new(_path, ErrorCodes.CaptureFail);

    public Result WriteAtomic(string json) => Fichier.WriteAtomic(json);

    public Result<string?> Read() => Fichier.Read();
}
