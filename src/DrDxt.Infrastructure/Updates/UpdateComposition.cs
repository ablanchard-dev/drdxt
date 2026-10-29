using System.Net.Http;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// Le montage du moteur de mise a jour : `AutoUpdater`, `HttpUpdateFeed`, `UpdateVerifier`, `LocalFileSystemUpdateStore`.
// Sans URL (ni configuree, ni par defaut), le montage refuse une fois, proprement : « pas configure » se corrige en
// configurant, un faux « hors ligne » a chaque tour enverrait chercher une panne qui n'existe pas.
// -----------------------------------------------------------------------
public static class UpdateComposition
{
    /// <summary>Le chemin du manifeste pour un canal, la route du serveur (`/updates/{canal}/manifest`, canal en
    /// minuscules), ecrite une seule fois : une copie divergente serait un 404 lu « rien de publie ».</summary>
    public static string ManifestPath(UpdateChannel channel)
        => "/updates/" + channel.ToString().ToLowerInvariant() + "/manifest";

    /// <summary>La racine garde son chemin : <c>new Uri(racine, "/updates/…")</c> ferait de
    /// https://ablanchard-dev.github.io/drdxt un https://ablanchard-dev.github.io/updates/…, un 404 lu « rien de
    /// publié ».</summary>
    public static string UrlDuManifeste(Uri racine, UpdateChannel channel)
        => racine.GetLeftPart(UriPartial.Path).TrimEnd('/') + ManifestPath(channel);

    /// <summary>Le dossier où l'installeur vérifié attend le clic (payload.pkg, puis DrDXT-Setup-&lt;v&gt;.exe).</summary>
    public static string DossierDuStagingApp(string localAppDataRoot)
        => Path.Combine(localAppDataRoot, "DrDXT", "staging", "app-staging");

    /// <summary>Monte l'updater, ou dit pourquoi il ne peut pas l'etre.</summary>
    /// <param name="manifestBaseUrl">Racine du serveur de mise a jour. Vide/absente ⇒ refus.</param>
    /// <param name="pinnedClient">Le client HTTP injecte, non epingle :
    /// aucune fabrique n'epingle le serveur de mise a jour, qui n'a pas encore d'empreintes.</param>
    /// <param name="localAppDataRoot">Racine des donnees locales (staging de l'installeur).</param>
    /// <param name="installedVersion">La version installee (regle de non-retrogradation).</param>
    public static Result<AutoUpdater> TryCreate(
        string? manifestBaseUrl,
        HttpClient pinnedClient,
        string localAppDataRoot,
        string installedVersion,
        // Les tests seuls fournissent un verificateur (la clef de dev). null = la clef de production
        // (UpdateTrust) ; absente ou de dev ⇒ aucun montage.
        IUpdateVerifier? verifier = null)
    {
        if (string.IsNullOrWhiteSpace(manifestBaseUrl))
        {
            return Result<AutoUpdater>.Fail(
                UpdateErrorCodes.NotConfigured,
                "No update server is configured; the app stays on its installed version.");
        }

        if (!Uri.TryCreate(manifestBaseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            // HTTPS exige : en clair, un intermediaire voit qui met a jour quoi, et peut retenir une mise a jour de securite
            // en repondant 204, invisible pour la signature.
            return Result<AutoUpdater>.Fail(
                UpdateErrorCodes.NotConfigured,
                "The update server url must be an absolute https url; refusing to fetch in clear.");
        }

        // Plus jamais la clef de dev en production : voir UpdateTrust.
        var porte = verifier is not null
            ? Result<IUpdateVerifier>.Ok(verifier)
            : UpdateTrust.CreateProductionVerifier();
        if (!porte.IsOk)
        {
            // Pas de clef de production (ou clef de dev) : rien n'est monte.
            return Result<AutoUpdater>.Fail(porte.Error!.Value.Code, porte.Error.Value.Message);
        }

        var feed = new HttpUpdateFeed(
            pinnedClient,
            channel => UrlDuManifeste(baseUri, channel));

        return Result<AutoUpdater>.Ok(new AutoUpdater(
            feed,
            porte.Value!,
            new LocalFileSystemUpdateStore(),
            Path.Combine(localAppDataRoot, "DrDXT", "staging"),
            installedVersion));
    }
}
