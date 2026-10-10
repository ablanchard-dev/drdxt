using System.Net.Http;
using System.Net.Http.Json;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// T13 — production seam implementations behind the AutoUpdater's interfaces.
// The updater is fully tested against fakes; these are the real wirings.
// -----------------------------------------------------------------------

/// <summary>The DEFERRED elevated binary-replacer. Mirrors the M2 deferred stubs:
/// the typed "replace signed files" operation needs the elevated helper + UAC wiring
/// (live câblage later). Until then it is FAIL-CLEAN — it replaces nothing and returns
/// Fail(UPDATE_REPLACER_UNAVAILABLE). The staged, verified payload is left intact so a
/// later live replacer can pick it up; there is never a half-applied install.</summary>
public sealed class DeferredElevatedFileReplacer : IElevatedFileReplacer
{
    public Task<Result> ReplaceAppFilesAsync(
        string stagedPayloadPath, string targetInstallDir, CancellationToken ct)
        => Task.FromResult(Result.Fail(
            UpdateErrorCodes.ReplacerUnavailable,
            "Elevated app-file replacement is not wired live yet (deferred, needs UAC). "
            + "The verified payload is staged and intact."));
}

/// <summary>The real %LOCALAPPDATA% filesystem seam for the profiles channel. Atomic
/// directory replace = move the old pack aside, move staging in, delete the old pack.
/// On ANY failure the previous live pack is restored / left intact (clean transaction).
/// Every member is Result-wrapped — IO failures never throw across the boundary.</summary>
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
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            // Atomic-ish per-file write: temp + move, so a crash never leaves a torn file.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(UpdateErrorCodes.StageFailed, $"WriteFile failed: {ex.Message}");
        }
    }

    public Result ReplaceDirectory(string stagingDir, string targetDir)
    {
        // Transactional swap: park the current pack, move staging in, then drop the park.
        // If the move-in fails, restore the parked pack so the old pack is never lost.
        var backup = targetDir + ".old-" + Guid.NewGuid().ToString("N");
        var parked = false;
        try
        {
            var parent = Path.GetDirectoryName(targetDir);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            if (Directory.Exists(targetDir))
            {
                Directory.Move(targetDir, backup);
                parked = true;
            }

            Directory.Move(stagingDir, targetDir);

            // Success — drop the parked old pack (best-effort; a leftover .old is harmless).
            if (parked && Directory.Exists(backup))
            {
                try { Directory.Delete(backup, recursive: true); } catch { /* non-fatal */ }
            }

            return Result.Ok();
        }
        catch (Exception ex)
        {
            // Roll back: restore the parked pack so the previous version stays intact.
            try
            {
                if (parked && !Directory.Exists(targetDir) && Directory.Exists(backup))
                    Directory.Move(backup, targetDir);
            }
            catch { /* best-effort restore; the original error is what we report */ }

            return Result.Fail(UpdateErrorCodes.StageFailed, $"ReplaceDirectory failed: {ex.Message}");
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

/// <summary>The real update feed over an INJECTED <see cref="HttpClient"/>. Any transport
/// failure → Fail(UPDATE_OFFLINE): non-blocking, the app stays on the current version.
///
/// 19/09/2026 — cette phrase disait « PINNED », AU PRESENT. Elle promettait que l'appelant
/// fournit un client epingle (T1). Mesure : le seul appelant reel, `App.xaml.cs`, construit un
/// `new HttpClient()` nu, et `PinnedHttpClientFactory` n'expose qu'un `CreateForKeygen()` dont
/// les empreintes visent `api.keygen.sh` — pas notre serveur de mise a jour, qui n'a pas encore
/// d'empreintes puisqu'il n'est pas deploye. `VerificationMiseAJour` le disait deja ; les deux
/// commentaires se contredisaient, et celui-ci avait tort.
///
/// Une doc fausse ne se contente pas d'etre fausse : elle repond a la question qu'un auditeur
/// venait poser, et l'empeche de trouver le trou. Ce que ce canal a reellement pour lui
/// aujourd'hui, chaque point etant tenu par un test : l'URL doit etre absolue et en HTTPS,
/// rien n'est monte sans clef de verification de PRODUCTION, le manifeste est signe en Ed25519
/// et le binaire verifie par son sha256. L'epinglage se posera avec le deploiement du serveur.
/// </summary>
public sealed class HttpUpdateFeed : IUpdateFeed
{
    // Le canal voyage en NOM d'enum (« App », « Profiles ») : c'est cette chaine-la que la
    // signature couvre des deux cotes. Sans ce convertisseur, System.Text.Json attend un nombre et
    // la lecture leve — echec que la couche au-dessus traduit en « hors ligne ». Le produit aurait
    // donc refuse toutes les mises a jour en accusant le reseau. Trouve le 06/09/2026 en branchant
    // le vrai client sur le vrai serveur ; aucun test de chaque cote ne pouvait le voir.
    private static readonly System.Text.Json.JsonSerializerOptions ManifestJson = new(
        System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly Func<UpdateChannel, string> _manifestUrl;

    /// <param name="pinnedClient">The injected HttpClient. Le nom garde « pinned » comme objectif ;
    /// l'epinglage de ce serveur n'existe pas encore (voir le resume de cette classe).</param>
    /// <param name="manifestUrlForChannel">Maps a channel to its manifest endpoint url.</param>
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

            // « Joignable, rien a offrir » — deux formes, et il a fallu brancher les deux bouts
            // pour s'en apercevoir. 404 = le canal n'existe pas ; 204 = le canal existe et aucune
            // version n'y est publiee, ce qui est l'etat reel du produit aujourd'hui. Le 204
            // tombait auparavant dans la branche « succes » et cassait sur un corps vide : le
            // client annoncait donc une panne de mise a jour a chaque demarrage d'une installation
            // parfaitement saine. Mesure du 06/09/2026, en faisant parler le vrai client au vrai
            // serveur (TheClientReadsTheRealServerTests).
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
            // Malformed JSON etc. — still non-fatal to the app; treat as offline (no update).
            return Result<UpdateManifest?>.Fail(UpdateErrorCodes.Offline, $"Update manifest unreadable: {ex.Message}");
        }
    }

    public const long TailleMaxOctets = 400L * 1024 * 1024;

    public async Task<Result<byte[]>> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            // 10/10/2026 (revue de sécurité) : une réponse sans fin faisait allouer des Go en mémoire. Un installeur
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
