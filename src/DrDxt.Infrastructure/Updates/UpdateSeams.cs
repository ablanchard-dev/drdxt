using System.Net.Http;
using System.Net.Http.Json;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// Les implémentations de production des coutures derrière les interfaces d'AutoUpdater.
// L'updater est entièrement testé avec des faux ; voici les vrais branchements.
// -----------------------------------------------------------------------

/// <summary>Le remplaçant élevé de binaires, reporté. Comme les autres coutures reportées :
/// l'opération typée « remplacer des fichiers signés » demande le helper élevé + le branchement de
/// l'UAC. D'ici là, il échoue proprement : il ne remplace rien et rend
/// Fail(UPDATE_REPLACER_UNAVAILABLE). La charge préparée et vérifiée reste intacte pour qu'un futur
/// remplaçant réel la reprenne ; jamais une installation à moitié appliquée.</summary>
public sealed class DeferredElevatedFileReplacer : IElevatedFileReplacer
{
    public Task<Result> ReplaceAppFilesAsync(
        string stagedPayloadPath, string targetInstallDir, CancellationToken ct)
        => Task.FromResult(Result.Fail(
            UpdateErrorCodes.ReplacerUnavailable,
            "Elevated app-file replacement is not wired live yet (deferred, needs UAC). "
            + "The verified payload is staged and intact."));
}

/// <summary>La vraie couture de système de fichiers %LOCALAPPDATA% pour la préparation. Chaque membre est emballé dans un Result : un échec d'E/S ne lève jamais à travers la frontière.</summary>
public sealed class LocalFileSystemUpdateStore : IUpdateFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public byte[]? TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    public Result CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(UpdateErrorCodes.StageFailed, $"CreateDirectory failed: {ex.Message}");
        }
    }

    public Result WriteFile(string path, byte[] bytes)
    {
        try
        {
            // Temporaire aléatoire + déplacement : un plantage ne laisse jamais un fichier déchiré, et aucun lien posé d'avance
            // n'est suivi.
            return new FichierJsonAtomique(path, UpdateErrorCodes.StageFailed).WriteAtomic(bytes);
        }
        catch (Exception ex)
        {
            return Result.Fail(UpdateErrorCodes.StageFailed, $"WriteFile failed: {ex.Message}");
        }
    }

    public Result DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(UpdateErrorCodes.StageFailed, $"DeleteDirectory failed: {ex.Message}");
        }
    }
}

/// <summary>Le vrai flux de mise à jour sur un <see cref="HttpClient"/> injecté. Toute panne de
/// transport → Fail(UPDATE_OFFLINE) : non bloquant, l'app reste sur la version actuelle.
///
/// Pas encore d'epinglage : le seul appelant reel, `App.xaml.cs`, construit un `new HttpClient()` nu,
/// sans empreinte de certificat pour le serveur de mise a jour.
///
/// Une doc fausse ne se contente pas d'etre fausse : elle repond a la question qu'un auditeur
/// venait poser, et l'empeche de trouver le trou. Ce que ce canal a reellement pour lui
/// aujourd'hui, chaque point etant tenu par un test : l'URL doit etre absolue et en HTTPS,
/// rien n'est monte sans clef de verification de production, le manifeste est signe en Ed25519
/// et le binaire verifie par son sha256. L'epinglage se posera avec le deploiement du serveur.
/// </summary>
public sealed class HttpUpdateFeed : IUpdateFeed
{
    // Le canal voyage en nom d'enum (« App ») : c'est cette chaine-la que la
    // signature couvre des deux cotes. Sans ce convertisseur, System.Text.Json attend un nombre et
    // la lecture leve, echec que la couche au-dessus traduit en « hors ligne » : le produit
    // refuserait toutes les mises a jour en accusant le reseau. Seul un essai du vrai client contre
    // le vrai serveur le montre ; un test de chaque cote ne le voit pas.
    private static readonly System.Text.Json.JsonSerializerOptions ManifestJson = new(
        System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly Func<UpdateChannel, string> _manifestUrl;

    /// <param name="pinnedClient">The injected HttpClient. Le nom garde « pinned » comme objectif ;
    /// l'epinglage de ce serveur n'existe pas encore (voir le resume de cette classe).</param>
    /// <param name="manifestUrlForChannel">Relie un canal à l'url de son manifeste.</param>
    public HttpUpdateFeed(HttpClient pinnedClient, Func<UpdateChannel, string> manifestUrlForChannel)
    {
        _http = pinnedClient;
        _manifestUrl = manifestUrlForChannel;
    }

    public async Task<Result<UpdateManifest?>> GetManifestAsync(UpdateChannel channel, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_manifestUrl(channel), ct).ConfigureAwait(false);

            // « Joignable, rien a offrir » : deux formes. 404 = le canal n'existe pas ; 204 = le canal existe
            // et aucune version n'y est publiee, ce qui est l'etat reel du produit aujourd'hui. Traite comme un
            // succes, le 204 casserait sur un corps vide : le client annoncerait une panne de mise a jour a
            // chaque demarrage d'une installation parfaitement saine. Mesure du 06/09/2026, en faisant parler
            // le vrai client au vrai serveur (TheClientReadsTheRealServerTests).
            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound
                                or System.Net.HttpStatusCode.NoContent)
                return Result<UpdateManifest?>.Ok(null);

            if (!resp.IsSuccessStatusCode)
                return Result<UpdateManifest?>.Fail(
                    UpdateErrorCodes.Offline, $"Update feed returned {(int)resp.StatusCode}.");

            var manifest = await resp.Content
                .ReadFromJsonAsync<UpdateManifest>(ManifestJson, ct)
                .ConfigureAwait(false);
            return Result<UpdateManifest?>.Ok(manifest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // No network / transport failure → offline (non-blocking).
            return Result<UpdateManifest?>.Fail(UpdateErrorCodes.Offline, $"Update feed unreachable: {ex.Message}");
        }
        catch (Exception ex)
        {
            // JSON malformé, etc. : toujours non fatal pour l'app ; traité comme hors ligne (pas de mise à jour).
            return Result<UpdateManifest?>.Fail(UpdateErrorCodes.Offline, $"Update manifest unreadable: {ex.Message}");
        }
    }

    public const long TailleMaxOctets = 400L * 1024 * 1024;

    public async Task<Result<byte[]>> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            // Une réponse sans fin faisait allouer des Go en mémoire. Un installeur
            // de DrDXT pèse moins de 200 Mo : au-delà, ce n'est pas une mise à jour.
            using var reponse = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            reponse.EnsureSuccessStatusCode();
            if (reponse.Content.Headers.ContentLength is > TailleMaxOctets)
                return Result<byte[]>.Fail(UpdateErrorCodes.Offline, "Update payload refused: larger than the size limit.");
            await using var flux = await reponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var memoire = new MemoryStream();
            var tampon = new byte[81920];
            int lu;
            while ((lu = await flux.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
            {
                if (memoire.Length + lu > TailleMaxOctets)
                    return Result<byte[]>.Fail(UpdateErrorCodes.Offline, "Update payload refused: larger than the size limit.");
                memoire.Write(tampon, 0, lu);
            }
            return Result<byte[]>.Ok(memoire.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return Result<byte[]>.Fail(UpdateErrorCodes.Offline, $"Update payload download failed: {ex.Message}");
        }
    }
}
