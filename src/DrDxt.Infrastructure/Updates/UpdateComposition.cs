using System.Net.Http;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// LU — le montage du moteur de mise a jour.
//
// Tout existait depuis M5 et rien n'etait assemble : `AutoUpdater`, `HttpUpdateFeed`,
// `UpdateVerifier`, `LocalFileSystemUpdateStore` n'etaient cites nulle part hors de leurs propres
// fichiers et de leurs tests. Un moteur qu'aucun code ne construit ne tourne pour personne, et rien
// dans une suite de tests verte ne le dit.
//
// ══ l'absence d'url est un etat, pas une panne ══
// Le serveur n'est deploye nulle part a ce jour (aucune URL dans le depot ; c'est un geste du mainteneur).
// Le montage doit donc pouvoir REFUSER de se faire, proprement et une seule fois, plutot que de
// produire un updater qui echouera a chaque tour en accusant le reseau. La difference n'est pas
// cosmetique : « pas configure » se corrige en configurant, « hors ligne » envoie chercher une
// panne qui n'existe pas.
// -----------------------------------------------------------------------
public static class UpdateComposition
{
    /// <summary>Le chemin du manifeste pour un canal — la meme chaine que la route du serveur
    /// (`/updates/{canal}/manifest`, canal en minuscules). Expose plutot que recopie : un chemin
    /// ecrit deux fois est un chemin qui divergera, et la divergence se presenterait au client
    /// comme un 404, c'est-a-dire comme « rien a offrir » — un silence, pas une erreur.</summary>
    public static string ManifestPath(UpdateChannel channel)
        => "/updates/" + channel.ToString().ToLowerInvariant() + "/manifest";

    /// <summary>09/10/2026 (revue) : la racine garde son chemin. <c>new Uri(racine, "/updates/…")</c> le remplaçait :
    /// https://ablanchard-dev.github.io/drdxt devenait https://ablanchard-dev.github.io/updates/…, un 404 lu comme
    /// « rien à offrir », et aucune copie installée n'aurait reçu de mise à jour.</summary>
    public static string UrlDuManifeste(Uri racine, UpdateChannel channel)
        => racine.GetLeftPart(UriPartial.Path).TrimEnd('/') + ManifestPath(channel);

    /// <summary>Monte l'updater, ou dit pourquoi il ne peut pas l'etre.</summary>
    /// <param name="manifestBaseUrl">Racine du serveur de mise a jour. Vide/absente ⇒ refus.</param>
    /// <param name="pinnedClient">Le client HTTP injecte. 19/09/2026 : ce texte disait « epingle » ;
    /// aucune fabrique n'epingle le serveur de mise a jour, qui n'a pas encore d'empreintes.</param>
    /// <param name="localAppDataRoot">Racine des donnees locales (profils + staging).</param>
    /// <param name="appInstallDir">Le dossier d'installation, pour l'echange eleve.</param>
    /// <param name="installedVersion">La version installee (regle de non-retrogradation).</param>
    public static Result<AutoUpdater> TryCreate(
        string? manifestBaseUrl,
        HttpClient pinnedClient,
        string localAppDataRoot,
        string appInstallDir,
        string installedVersion,
        // Les TESTS seuls fournissent un verificateur (la clef de dev). null = la clef de PRODUCTION
        // (UpdateTrust), absente aujourd'hui ⇒ aucun montage.
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
            // HTTPS EXIGE. La signature protege deja le contenu du manifeste — mais en clair, un
            // intermediaire voit qui met a jour quoi et quand, et peut retenir indefiniment une
            // mise a jour de securite en repondant 204. Un refus de servir est invisible pour une
            // signature.
            return Result<AutoUpdater>.Fail(
                UpdateErrorCodes.NotConfigured,
                "The update server url must be an absolute https url; refusing to fetch in clear.");
        }

        // Plus jamais la clef de dev en production (revue du 10/09/2026) : voir UpdateTrust.
        var porte = verifier is not null
            ? Result<IUpdateVerifier>.Ok(verifier)
            : UpdateTrust.CreateProductionVerifier();
        if (!porte.IsOk)
        {
            // Pas de clef de production (ou clef de dev) : on ne monte rien. Sans verificateur de
            // confiance, un moteur de mise a jour est pire qu'aucun moteur.
            return Result<AutoUpdater>.Fail(porte.Error!.Value.Code, porte.Error.Value.Message);
        }

        var feed = new HttpUpdateFeed(
            pinnedClient,
            channel => UrlDuManifeste(baseUri, channel));

        return Result<AutoUpdater>.Ok(new AutoUpdater(
            feed,
            porte.Value!,
            new LocalFileSystemUpdateStore(),
            new DeferredElevatedFileReplacer(),
            Path.Combine(localAppDataRoot, "DrDXT", "profiles"),
            Path.Combine(localAppDataRoot, "DrDXT", "staging"),
            appInstallDir,
            installedVersion));
    }
}
