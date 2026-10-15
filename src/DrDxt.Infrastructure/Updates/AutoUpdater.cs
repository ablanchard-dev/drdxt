using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// Mise à jour signée de l'app : vérifier la signature du manifeste → télécharger → vérifier
// l'empreinte de la charge → la préparer atomiquement → déléguer l'échange dans Program Files à la
// couture typée IElevatedFileReplacer (« remplacer des fichiers signés », pas une écriture
// quelconque). Un échec de vérification à n'importe quelle étape veut dire qu'aucun fichier n'est remplacé.
//
// Aucun canal de contenu silencieux : tout changement de ce que fait DrDXT arrive comme une
// nouvelle version de l'app, que le joueur installe d'un clic.
//
// Jamais fail-open, jamais bloquant : pas de réseau → Fail(UPDATE_OFFLINE) et l'app continue sur
// la version actuelle. Aucun manifeste offert → UpToDate (sans effet). Chaque issue est un Result :
// les frontières crypto / HTTP / disque ne lèvent jamais à travers la frontière.
// -----------------------------------------------------------------------

/// <summary>L'issue d'une vérification de canal : un petit statut testable que l'interface /
/// l'hôte traduit en message. Rien n'est levé ; l'updater est total.</summary>
public enum UpdateOutcome
{
    /// <summary>Le flux n'offre aucun manifeste plus récent : déjà à jour (sans effet).</summary>
    UpToDate,

    /// <summary>La charge de l'app est vérifiée et préparée atomiquement, prête pour que le
    /// remplaçant élevé échange les binaires de Program Files.</summary>
    AppStaged,
}

/// <summary>Mène le canal de mise à jour de l'app derrière des coutures (<see cref="IUpdateFeed"/>,
/// <see cref="IUpdateVerifier"/>, <see cref="IUpdateFileSystem"/>,
/// <see cref="IElevatedFileReplacer"/>), toutes simulables en test. Jamais bloquant,
/// jamais fail-open.</summary>
public sealed class AutoUpdater
{
    private readonly IUpdateFeed _feed;
    private readonly IUpdateVerifier _verifier;
    private readonly IUpdateFileSystem _fs;
    private readonly IElevatedFileReplacer _replacer;
    private readonly string _stagingRoot;
    private readonly string _appInstallDir;
    private readonly string _installedVersion;

    /// <param name="feed">Récupère le manifeste signé + la charge par le transport injecté.
    /// Le transport n'est pas epingle : ce serveur n'a pas encore d'empreintes.</param>
    /// <param name="verifier">La barrière de confiance (signature puis empreinte).</param>
    /// <param name="fs">Couture de système de fichiers (préparation / remplacement atomiques), simulable.</param>
    /// <param name="replacer">Couture typée et élevée d'échange de binaires (reportée, échec propre).</param>
    /// <param name="stagingRoot">Racine de brouillon pour la préparation (voisine des dossiers en service).</param>
    /// <param name="appInstallDir">Dossier d'installation dans Program Files (pour l'échange élevé).</param>
    /// <param name="installedVersion">La version installée (règle de non-rétrogradation).</param>
    public AutoUpdater(
        IUpdateFeed feed,
        IUpdateVerifier verifier,
        IUpdateFileSystem fs,
        IElevatedFileReplacer replacer,
        string stagingRoot,
        string appInstallDir,
        string installedVersion)
    {
        _feed = feed;
        _verifier = verifier;
        _fs = fs;
        _replacer = replacer;
        _stagingRoot = stagingRoot;
        _appInstallDir = appInstallDir;
        _installedVersion = installedVersion;
    }

    /// <summary>Vérifie et prépare le canal de l'app : vérifier → télécharger → vérifier l'empreinte
    /// → préparer atomiquement → déléguer l'échange dans Program Files au remplaçant typé et élevé. Si
    /// la vérification échoue à n'importe quelle étape, aucun fichier n'est remplacé. Pour l'instant le
    /// remplaçant est reporté (échec propre) : la méthode remonte UPDATE_REPLACER_UNAVAILABLE avec la
    /// charge préparée en sûreté, jamais une installation à moitié appliquée.</summary>
    /// <param name="telechargement">Borne du téléchargement de l'installeur seul ; par défaut, celle du manifeste.</param>
    public async Task<Result<UpdateOutcome>> UpdateAppAsync(CancellationToken ct, CancellationToken? telechargement = null)
    {
        var gated = await GateAsync(ct, telechargement ?? ct);
        if (!gated.IsOk)
            return Result<UpdateOutcome>.Fail(gated.Error!.Value.Code, gated.Error.Value.Message);
        if (gated.Value is null)
            return Result<UpdateOutcome>.Ok(UpdateOutcome.UpToDate);

        var (_, payload) = gated.Value.Value;

        // Préparer atomiquement la charge vérifiée avant toute tentative de remplacement.
        var staging = CombinePath(_stagingRoot, "app-staging");   // meme chemin que StagedAppPayloadPath
        var stage = StagePayload(payload, staging);
        if (!stage.IsOk)
        {
            _fs.DeleteDirectory(staging);
            return Result<UpdateOutcome>.Fail(stage.Error!.Value.Code, stage.Error.Value.Message);
        }

        // Déléguer l'échange privilégié de binaires à la couture typée et admise. Reporté pour l'instant
        // → Fail(UPDATE_REPLACER_UNAVAILABLE), charge préparée et intacte. L'échange suit le téléchargement et prend sa borne :
        // celle du manifeste (quelques secondes) est déjà passée après un long téléchargement.
        var replace = await _replacer.ReplaceAppFilesAsync(stage.Value!, _appInstallDir, telechargement ?? ct);
        if (!replace.IsOk)
            return Result<UpdateOutcome>.Fail(replace.Error!.Value.Code, replace.Error.Value.Message);

        return Result<UpdateOutcome>.Ok(UpdateOutcome.AppStaged);
    }

    // -------------------------------------------------------------------
    // La barrière de confiance : récupérer le manifeste signé, vérifier d'abord sa signature, puis
    // télécharger la charge et vérifier son empreinte (+ non-rétrogradation). Rend Ok(null) quand le
    // flux est joignable mais n'offre rien de plus récent. Tout échec → Fail ; rien n'est jamais
    // appliqué dans le doute.
    // -------------------------------------------------------------------
    private async Task<Result<(UpdateManifest Manifest, byte[] Payload)?>> GateAsync(
        CancellationToken ct, CancellationToken telechargement)
    {
        var manifestResult = await _feed.GetManifestAsync(UpdateChannel.App, ct);
        if (!manifestResult.IsOk)
            // Hors ligne (ou toute panne de transport) → non bloquant ; l'app reste sur sa version actuelle.
            return Result<(UpdateManifest, byte[])?>.Fail(
                manifestResult.Error!.Value.Code, manifestResult.Error.Value.Message);

        var manifest = manifestResult.Value;
        if (manifest is null)
            return Result<(UpdateManifest, byte[])?>.Ok(null); // already current

        // Le canal fait partie des octets signés : un manifeste signé pour autre chose est refusé.
        if (manifest.Channel != UpdateChannel.App)
            return Result<(UpdateManifest, byte[])?>.Fail(UpdateErrorCodes.SignatureInvalid,
                $"Update manifest is for channel {manifest.Channel}, not App.");

        // (1) La signature d'abord : on ne télécharge même pas la charge depuis une url non fiable.
        var sig = _verifier.VerifyManifest(manifest);
        if (!sig.IsOk)
            return Result<(UpdateManifest, byte[])?>.Fail(sig.Error!.Value.Code, sig.Error.Value.Message);

        // Le site sert toujours le dernier manifeste. La version déjà installée n'est pas une
        // nouvelle version : sans ce test, elle était retéléchargée et « Installer » la reproposait à chaque démarrage.
        if (!UpdateVerifier.IsDowngrade(manifest.Version, _installedVersion)
            && !UpdateVerifier.IsDowngrade(_installedVersion, manifest.Version))
            return Result<(UpdateManifest, byte[])?>.Ok(null);

        // (2) Seulement maintenant, récupérer la charge (l'url vient d'un manifeste signé).
        // Tant que l'installation est manuelle, le canal App retelechargerait la meme charge a chaque
        // demarrage : une charge deja stagee avec l'empreinte du manifeste signe est reprise, et repasse
        // la verification (3) comme une charge neuve.
        byte[] payload;
        var existante = _fs.TryReadFile(StagedAppPayloadPath);
        if (existante is not null && string.Equals(Sha256Hex(existante), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            payload = existante;
        }
        else
        {
            var download = await _feed.DownloadAsync(manifest.Url, telechargement);
            if (!download.IsOk)
                return Result<(UpdateManifest, byte[])?>.Fail(
                    download.Error!.Value.Code, download.Error.Value.Message);
            payload = download.Value!;
        }

        // (3) Empreinte (+ non-rétrogradation) contre le manifeste désormais fiable. Désaccord → jamais utilisée.
        var hash = _verifier.VerifyPayload(manifest, payload, _installedVersion, allowDowngrade: false);
        if (!hash.IsOk)
            return Result<(UpdateManifest, byte[])?>.Fail(hash.Error!.Value.Code, hash.Error.Value.Message);

        DerniereVersionApp = manifest;
        return Result<(UpdateManifest, byte[])?>.Ok((manifest, payload));
    }

    /// <summary>Le manifeste App verifie au dernier passage (signature, empreinte, non-retrogradation).
    /// null = aucune version App offerte ou verifiee. Sert au bouton « Installer ».</summary>
    public UpdateManifest? DerniereVersionApp { get; private set; }

    /// <summary>Ou la charge App verifiee est stagee (un installeur signe, DrDXT-Setup.exe).</summary>
    public string StagedAppPayloadPath => CombinePath(CombinePath(_stagingRoot, "app-staging"), "payload.pkg");

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    // Préparer une charge brute de l'app (déjà vérifiée) comme un seul fichier dans un dossier de
    // préparation neuf, et rendre son chemin pour le remplaçant élevé. L'échange privilégié est le
    // travail du remplaçant ; la préparation n'est pas privilégiée.
    private Result<string> StagePayload(byte[] payload, string stagingDir)
    {
        var create = ResetStaging(stagingDir);
        if (!create.IsOk)
            return Result<string>.Fail(create.Error!.Value.Code, create.Error.Value.Message);

        var path = CombinePath(stagingDir, "payload.pkg");
        var write = _fs.WriteFile(path, payload);
        if (!write.IsOk)
            return Result<string>.Fail(write.Error!.Value.Code, write.Error.Value.Message);

        return Result<string>.Ok(path);
    }

    // S'assurer d'un dossier de préparation propre : retirer tout reste d'un passage précédent, puis créer.
    private Result ResetStaging(string stagingDir)
    {
        if (_fs.DirectoryExists(stagingDir))
            _fs.DeleteDirectory(stagingDir); // au mieux ; la création ci-dessous est la vraie garde
        return _fs.CreateDirectory(stagingDir);
    }

    // --- Aides de chemin (aucun accès disque réel ; sûres sous une couture de disque simulée) -----------------

    private static string CombinePath(string a, string b)
        => Path.Combine(a, b);
}
