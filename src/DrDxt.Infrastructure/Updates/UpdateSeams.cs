using System.Net.Http;
using System.Net.Http.Json;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// Les implémentations de production des coutures derrière les interfaces d'AutoUpdater.
// L'updater est entièrement testé avec des faux ; voici les vrais branchements.
// -----------------------------------------------------------------------

/// <summary>La vraie couture de système de fichiers %LOCALAPPDATA% pour la préparation. Chaque membre est emballé dans un Result : un échec d'E/S ne lève jamais à travers la frontière.</summary>
public sealed class LocalFileSystemUpdateStore : IUpdateFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public byte[]? TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    // Le staging vit dans le dossier du compte : élevé, rien ne s'y crée ni ne s'y vide à travers une jonction.
    public Result CreateDirectory(string path)
    {
        try
        {
            if (FichierJsonAtomique.RefusEleve(path, UpdateErrorCodes.StageFailed) is { } refus) return refus;
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
            if (FichierJsonAtomique.RefusEleve(path, UpdateErrorCodes.StageFailed) is { } refus) return refus;
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
/// Pas encore d'epinglage : le seul appelant reel, VerificationMiseAJour.VerifierAuDemarrageAsync, construit son
/// HttpClient sur le transport du compte (ProxyDuCompte), sans empreinte de certificat pour le serveur de mise a jour.
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
            // Lu avant d'être vérifié : la réponse s'arrête au plafond au lieu d'être gardée en entier en mémoire.
            using var resp = await _http.GetAsync(_manifestUrl(channel), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // 204 : le serveur dit que le canal existe et qu'aucune version n'y est publiée, « rien à offrir ». Traité
            // comme un succès, il casserait sur un corps vide : le client annoncerait une panne à chaque démarrage d'une
            // installation saine (TheClientReadsTheRealServerTests, le vrai client contre le vrai serveur).
            if (resp.StatusCode is System.Net.HttpStatusCode.NoContent)
                return Result<UpdateManifest?>.Ok(null);
            // 404 : rien n'est publié à cette adresse, que le canal soit sain (site statique sans manifeste) ou cassé.
            // Ce n'est ni « à jour » ni une panne : un état à part, qui ne dit que ça.
            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound)
                return Result<UpdateManifest?>.Fail(UpdateErrorCodes.NotPublished, "No update manifest is published at this address.");

            if (!resp.IsSuccessStatusCode)
                return Result<UpdateManifest?>.Fail(
                    UpdateErrorCodes.Offline, $"Update feed returned {(int)resp.StatusCode}.");

            if (resp.Content.Headers.ContentLength is > ManifesteOctetsMax)
                return Result<UpdateManifest?>.Fail(UpdateErrorCodes.Offline, "Update manifest refused: larger than the size limit.");
            var octets = await LireAuPlusAsync(resp.Content, ManifesteOctetsMax, ct).ConfigureAwait(false);
            if (octets is null)
                return Result<UpdateManifest?>.Fail(UpdateErrorCodes.Offline, "Update manifest refused: larger than the size limit.");
            var manifest = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(octets, ManifestJson);
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

    /// <summary>Un manifeste tient en quelques centaines d'octets (canal, version, adresse, empreinte, signature).</summary>
    public const int ManifesteOctetsMax = 64 * 1024;

    public const long TailleMaxOctets = 400L * 1024 * 1024;

    public async Task<Result<byte[]>> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            // L'adresse vient du manifeste signé ; elle est quand même exigée en HTTPS, et une adresse illisible est un refus,
            // pas une exception.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var adresse) || adresse.Scheme != Uri.UriSchemeHttps)
                return Result<byte[]>.Fail(UpdateErrorCodes.Offline, "Update payload refused: its address is not HTTPS.");
            // Une réponse sans fin faisait allouer des Go en mémoire. Un installeur
            // de DrDXT pèse moins de 200 Mo : au-delà, ce n'est pas une mise à jour.
            using var reponse = await _http.GetAsync(adresse, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            reponse.EnsureSuccessStatusCode();
            if (reponse.Content.Headers.ContentLength is > TailleMaxOctets)
                return Result<byte[]>.Fail(UpdateErrorCodes.Offline, "Update payload refused: larger than the size limit.");
            var octets = await LireAuPlusAsync(reponse.Content, TailleMaxOctets, ct).ConfigureAwait(false);
            return octets is null
                ? Result<byte[]>.Fail(UpdateErrorCodes.Offline, "Update payload refused: larger than the size limit.")
                : Result<byte[]>.Ok(octets);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return Result<byte[]>.Fail(UpdateErrorCodes.Offline, $"Update payload download failed: {ex.Message}");
        }
    }

    /// <summary>Le corps lu par morceaux, null dès qu'il dépasse <paramref name="plafond"/>.</summary>
    private static async Task<byte[]?> LireAuPlusAsync(HttpContent contenu, long plafond, CancellationToken ct)
    {
        await using var flux = await contenu.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memoire = new MemoryStream();
        var tampon = new byte[81920];
        int lu;
        while ((lu = await flux.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
        {
            if (memoire.Length + lu > plafond)
                return null;
            memoire.Write(tampon, 0, lu);
        }
        return memoire.ToArray();
    }
}
