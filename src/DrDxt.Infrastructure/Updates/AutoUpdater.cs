using System.IO.Compression;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// T13 — Two-channel signed auto-updater. Both channels run the same T12 gate
// (verify manifest signature → download → verify payload hash) and only then apply,
// differently per channel:
//
//   * Profiles channel (real, no admin): atomically extract the verified pack into
//     %LOCALAPPDATA%\DrDXT\profiles\. A corrupt/failed pack leaves the OLD pack intact
//     (clean transaction — never a half-applied state). This is the recurring value
//     without a reinstall (§18).
//   * App channel: verify → stage atomically → delegate the Program Files binary swap
//     to the TYPED IElevatedFileReplacer seam ("replace signed files", not an arbitrary
//     write). For M5 the live elevation/helper is DEFERRED (fail-clean), like the M2
//     stubs. A verification failure at ANY step means NO file is replaced.
//
// Jamais fail-open, never blocking: no network → Fail(UPDATE_OFFLINE) and the app keeps
// running on the CURRENT version. No manifest offered → UpToDate (no-op). Every result
// is a Result so the crypto/HTTP/FS frontiers never throw across the boundary.
// -----------------------------------------------------------------------

/// <summary>The outcome of a single channel check — a small, testable status the UI/host
/// maps to messaging. It is not thrown; the updater is total.</summary>
public enum UpdateOutcome
{
    /// <summary>The feed offered no newer manifest — already current (no-op).</summary>
    UpToDate,

    /// <summary>The profiles pack was verified and atomically installed.</summary>
    ProfilesInstalled,

    /// <summary>The app payload was verified and atomically STAGED, ready for the
    /// elevated replacer to swap the Program Files binaries.</summary>
    AppStaged,
}

/// <summary>Drives the two update channels behind seams (<see cref="IUpdateFeed"/>,
/// <see cref="IUpdateVerifier"/>, <see cref="IUpdateFileSystem"/>,
/// <see cref="IElevatedFileReplacer"/>) — all fakeable in tests. Never blocking,
/// never fail-open.</summary>
public sealed class AutoUpdater
{
    private readonly IUpdateFeed _feed;
    private readonly IUpdateVerifier _verifier;
    private readonly IUpdateFileSystem _fs;
    private readonly IElevatedFileReplacer _replacer;
    private readonly string _profilesDir;
    private readonly string _stagingRoot;
    private readonly string _appInstallDir;
    private readonly string _installedVersion;

    /// <param name="feed">Fetches the signed manifest + payload over the injected transport.
    /// (19/09/2026 : ce texte disait « pinned » ; l epinglage de ce serveur n existe pas encore.)</param>
    /// <param name="verifier">The T12 gate (signature then hash).</param>
    /// <param name="fs">Filesystem seam (atomic staging/replace), fakeable.</param>
    /// <param name="replacer">Typed elevated binary-swap seam (deferred fail-clean in M5).</param>
    /// <param name="profilesDir">Live profiles dir (%LOCALAPPDATA%\DrDXT\profiles).</param>
    /// <param name="stagingRoot">Scratch root for staging (sibling of the live dirs).</param>
    /// <param name="appInstallDir">Program Files install dir (for the elevated swap).</param>
    /// <param name="installedVersion">The currently-installed version (no-downgrade rule).</param>
    public AutoUpdater(
        IUpdateFeed feed,
        IUpdateVerifier verifier,
        IUpdateFileSystem fs,
        IElevatedFileReplacer replacer,
        string profilesDir,
        string stagingRoot,
        string appInstallDir,
        string installedVersion)
    {
        _feed = feed;
        _verifier = verifier;
        _fs = fs;
        _replacer = replacer;
        _profilesDir = profilesDir;
        _stagingRoot = stagingRoot;
        _appInstallDir = appInstallDir;
        _installedVersion = installedVersion;
    }

    /// <summary>Checks and applies the profiles channel: verify (T12) → download →
    /// verify hash → atomically extract into the live profiles dir. A corrupt/failed
    /// pack leaves the previous pack intact. No admin. Never blocking.</summary>
    public async Task<Result<UpdateOutcome>> UpdateProfilesAsync(CancellationToken ct)
    {
        var gated = await GateAsync(UpdateChannel.Profiles, allowDowngrade: false, ct);
        if (!gated.IsOk)
            return Result<UpdateOutcome>.Fail(gated.Error!.Value.Code, gated.Error.Value.Message);
        if (gated.Value is null)
            return Result<UpdateOutcome>.Ok(UpdateOutcome.UpToDate); // nothing offered

        var (_, payload) = gated.Value.Value;

        // Stage the extracted pack into a fresh staging dir, then atomically swap it in.
        // EVERYTHING goes through the fs seam, so a fake FS proves the transaction.
        var staging = CombinePath(_stagingRoot, "profiles-staging");

        var extract = ExtractZipToStaging(payload, staging);
        if (!extract.IsOk)
        {
            _fs.DeleteDirectory(staging); // best-effort cleanup; old pack untouched
            return Result<UpdateOutcome>.Fail(extract.Error!.Value.Code, extract.Error.Value.Message);
        }

        var swap = _fs.ReplaceDirectory(staging, _profilesDir);
        if (!swap.IsOk)
        {
            _fs.DeleteDirectory(staging);
            // ReplaceDirectory's contract: on failure the previous target is untouched.
            return Result<UpdateOutcome>.Fail(UpdateErrorCodes.StageFailed, swap.Error!.Value.Message);
        }

        return Result<UpdateOutcome>.Ok(UpdateOutcome.ProfilesInstalled);
    }

    /// <summary>Checks and stages the app channel: verify (T12) → download → verify hash
    /// → stage atomically → delegate the Program Files swap to the typed elevated
    /// replacer. If verification fails at ANY step, NO file is replaced. In M5 the
    /// replacer is deferred (fail-clean), so this surfaces UPDATE_REPLACER_UNAVAILABLE
    /// with the payload safely staged — never a half-applied install.</summary>
    public async Task<Result<UpdateOutcome>> UpdateAppAsync(CancellationToken ct)
    {
        var gated = await GateAsync(UpdateChannel.App, allowDowngrade: false, ct);
        if (!gated.IsOk)
            return Result<UpdateOutcome>.Fail(gated.Error!.Value.Code, gated.Error.Value.Message);
        if (gated.Value is null)
            return Result<UpdateOutcome>.Ok(UpdateOutcome.UpToDate);

        var (_, payload) = gated.Value.Value;

        // Stage the verified payload atomically before any replacement is attempted.
        var staging = CombinePath(_stagingRoot, "app-staging");   // meme chemin que StagedAppPayloadPath
        var stage = StagePayload(payload, staging);
        if (!stage.IsOk)
        {
            _fs.DeleteDirectory(staging);
            return Result<UpdateOutcome>.Fail(stage.Error!.Value.Code, stage.Error.Value.Message);
        }

        // Delegate the privileged binary swap to the typed, allow-listed seam. Deferred
        // in M5 → Fail(UPDATE_REPLACER_UNAVAILABLE) with the payload staged & intact.
        var replace = await _replacer.ReplaceAppFilesAsync(stage.Value!, _appInstallDir, ct);
        if (!replace.IsOk)
            return Result<UpdateOutcome>.Fail(replace.Error!.Value.Code, replace.Error.Value.Message);

        return Result<UpdateOutcome>.Ok(UpdateOutcome.AppStaged);
    }

    // -------------------------------------------------------------------
    // The shared T12 gate: fetch the signed manifest, verify its SIGNATURE first, then
    // download the payload and verify its HASH (+ no-downgrade). Returns Ok(null) when
    // the feed is reachable but offers nothing newer. ANY failure → Fail; nothing is
    // ever applied on doubt.
    // -------------------------------------------------------------------
    private async Task<Result<(UpdateManifest Manifest, byte[] Payload)?>> GateAsync(
        UpdateChannel channel, bool allowDowngrade, CancellationToken ct)
    {
        var manifestResult = await _feed.GetManifestAsync(channel, ct);
        if (!manifestResult.IsOk)
            // Offline (or any transport failure) → non-blocking; the app stays current.
            return Result<(UpdateManifest, byte[])?>.Fail(
                manifestResult.Error!.Value.Code, manifestResult.Error.Value.Message);

        var manifest = manifestResult.Value;
        if (manifest is null)
            return Result<(UpdateManifest, byte[])?>.Ok(null); // already current

        // Revue du 09/10/2026 : un manifeste signé pour un canal, servi sur l'autre, passait la signature et l'empreinte
        // (un pack de profils aurait pu être pris pour une App). Le canal fait partie des octets signés : il doit être le bon.
        if (manifest.Channel != channel)
            return Result<(UpdateManifest, byte[])?>.Fail(UpdateErrorCodes.SignatureInvalid,
                $"Update manifest is for channel {manifest.Channel}, served on channel {channel}.");

        // (1) Signature first — we do not even download the payload from an untrusted url.
        var sig = _verifier.VerifyManifest(manifest);
        if (!sig.IsOk)
            return Result<(UpdateManifest, byte[])?>.Fail(sig.Error!.Value.Code, sig.Error.Value.Message);

        // 09/10/2026 (revue) : le site sert toujours le dernier manifeste. La version déjà installée n'est pas une
        // nouvelle version : sans ce test, elle était retéléchargée et « Installer » la reproposait à chaque démarrage.
        if (channel == UpdateChannel.App && !UpdateVerifier.IsDowngrade(manifest.Version, _installedVersion)
            && !UpdateVerifier.IsDowngrade(_installedVersion, manifest.Version))
            return Result<(UpdateManifest, byte[])?>.Ok(null);

        // (2) Only now fetch the payload (the url came from a SIGNED manifest).
        // Revue du 10/09/2026 : tant que l'installation est manuelle, le canal App retelechargeait
        // la meme charge a chaque demarrage. Une charge deja stagee avec l'empreinte du manifeste signe
        // est reprise — et repasse la verification (3) comme une charge neuve.
        byte[] payload;
        var existante = channel == UpdateChannel.App ? _fs.TryReadFile(StagedAppPayloadPath) : null;
        if (existante is not null && string.Equals(Sha256Hex(existante), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            payload = existante;
        }
        else
        {
            var download = await _feed.DownloadAsync(manifest.Url, ct);
            if (!download.IsOk)
                return Result<(UpdateManifest, byte[])?>.Fail(
                    download.Error!.Value.Code, download.Error.Value.Message);
            payload = download.Value!;
        }

        // (3) Hash (+ no-downgrade) against the now-trusted manifest. Mismatch → never used.
        var hash = _verifier.VerifyPayload(manifest, payload, _installedVersion, allowDowngrade);
        if (!hash.IsOk)
            return Result<(UpdateManifest, byte[])?>.Fail(hash.Error!.Value.Code, hash.Error.Value.Message);

        if (channel == UpdateChannel.App) DerniereVersionApp = manifest;
        return Result<(UpdateManifest, byte[])?>.Ok((manifest, payload));
    }

    /// <summary>Le manifeste App VERIFIE au dernier passage (signature, empreinte, non-retrogradation).
    /// null = aucune version App offerte ou verifiee. Sert au bouton « Installer ».</summary>
    public UpdateManifest? DerniereVersionApp { get; private set; }

    /// <summary>Ou la charge App verifiee est stagee (un installeur signe, DrDXT-Setup.exe).</summary>
    public string StagedAppPayloadPath => CombinePath(CombinePath(_stagingRoot, "app-staging"), "payload.pkg");

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    // Extract a verified zip payload entry-by-entry into a fresh staging dir, routing all
    // FS writes through the seam. Zip parsing runs on the in-memory bytes (pure). A bad
    // archive or a path-traversal entry → Fail (old pack untouched, never throws).
    private Result ExtractZipToStaging(byte[] payload, string stagingDir)
    {
        var create = ResetStaging(stagingDir);
        if (!create.IsOk) return create;

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                // Skip directory entries (created implicitly by their files).
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                // Defense: reject any entry whose full path escapes the staging dir
                // (zip-slip). Never write outside the staging sandbox.
                var dest = SafeCombine(stagingDir, entry.FullName);
                if (dest is null)
                    return Result.Fail(UpdateErrorCodes.StageFailed,
                        $"Update pack entry escapes the staging directory: {entry.FullName}");

                var dir = GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir) && !_fs.DirectoryExists(dir))
                {
                    var mk = _fs.CreateDirectory(dir);
                    if (!mk.IsOk) return mk;
                }

                byte[] bytes;
                using (var es = entry.Open())
                using (var buf = new MemoryStream())
                {
                    es.CopyTo(buf);
                    bytes = buf.ToArray();
                }

                var write = _fs.WriteFile(dest, bytes);
                if (!write.IsOk) return write;
            }

            return Result.Ok();
        }
        catch (InvalidDataException ex)
        {
            // Corrupt / not-a-zip payload — fail-clean, old pack untouched.
            return Result.Fail(UpdateErrorCodes.StageFailed, $"Update pack is not a valid archive: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result.Fail(UpdateErrorCodes.StageFailed, $"Update pack extraction failed: {ex.Message}");
        }
    }

    // Stage a raw (already-verified) app payload as a single file in a fresh staging dir,
    // returning its staged path for the elevated replacer. The privileged swap is the
    // replacer's job; staging is unprivileged.
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

    // Ensure a CLEAN staging dir: remove any leftover from a previous run, then create.
    private Result ResetStaging(string stagingDir)
    {
        if (_fs.DirectoryExists(stagingDir))
            _fs.DeleteDirectory(stagingDir); // best-effort; create below is the real guard
        return _fs.CreateDirectory(stagingDir);
    }

    // --- Path helpers (no real FS access; safe under a fake fs seam) -----------------

    private static string CombinePath(string a, string b)
        => Path.Combine(a, b);

    private static string? GetDirectoryName(string path)
        => Path.GetDirectoryName(path);

    // Combine + reject path traversal. Returns null if `relative` escapes `root`.
    private static string? SafeCombine(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        var rootWithSep = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootWithSep, StringComparison.Ordinal) ? combined : null;
    }
}
