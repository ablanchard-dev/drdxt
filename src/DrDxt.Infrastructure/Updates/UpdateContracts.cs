using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// M5 / T12-T13 — the signed update manifest + the seams the updater runs on.
//
// SECURITY MODEL (master plan §9 / plan 05 §1):
//   * A manifest is the only thing the client trusts to learn where a payload is
//     (url), what it must hash to (sha256) and what version it is. It is signed with
//     a DEDICATED update Ed25519 key (UpdateDevKeys) — verified before we act on any
//     of its fields. We only ever trust a hash that came from a SIGNED manifest.
//   * Order is IMPOSED: verify the manifest SIGNATURE first; only if Ok do we fetch
//     the payload and check its SHA-256 against the (now-trusted) manifest hash.
//   * jamais fail-open: any uncertainty keeps the CURRENT installed version.
//
// Every seam returns a Result so an unavailable network / FS / crypto path fails
// CLEAN (never throws across the boundary).
// -----------------------------------------------------------------------

/// <summary>The two independent update channels. They are verified by the same gate
/// (T12) but applied differently (T13): <see cref="App"/> needs an elevated binary
/// swap; <see cref="Profiles"/> writes under %LOCALAPPDATA% with no admin.</summary>
public enum UpdateChannel
{
    /// <summary>The app binary in Program Files. Replacing it needs elevation, so the
    /// swap is delegated to the typed <see cref="IElevatedFileReplacer"/> seam.</summary>
    App,

    /// <summary>The profile packs under %LOCALAPPDATA%\DrDXT\profiles. Extracted with
    /// NO admin — the recurring value without a reinstall (§18).</summary>
    Profiles,
}

/// <summary>The signed update manifest. The signature (Ed25519, base64) is over the
/// CANONICAL bytes of the four trusted fields (channel, version, url, sha256). The
/// client verifies it with the embedded update PUBLIC key (T12) before touching the
/// url or trusting the sha256.</summary>
/// <param name="Channel">Which channel this manifest describes.</param>
/// <param name="Version">The offered version (semantic; compared against installed).</param>
/// <param name="Url">Where the payload lives. Never acted on until the signature verifies.</param>
/// <param name="Sha256">The expected lowercase-hex SHA-256 of the payload. Only trusted
/// after the signature verifies (we never trust a hash from an unsigned manifest).</param>
/// <param name="Signature">Detached Ed25519 signature (base64) over the canonical bytes.</param>
public sealed record UpdateManifest(
    UpdateChannel Channel,
    string Version,
    string Url,
    string Sha256,
    string Signature);

/// <summary>The update TRUST GATE (T12). Verifies the manifest signature first, then
/// (separately, only when the caller has the bytes) the payload hash. Result-based and
/// total: any tamper / wrong key / absent signature / hash mismatch / downgrade →
/// Fail(UPDATE_*), never Ok. Jamais fail-open.</summary>
public interface IUpdateVerifier
{
    /// <summary>Step 1 — verify the manifest's Ed25519 signature under the embedded
    /// update public key. Ok iff the signature is valid over the canonical manifest
    /// bytes. A null/empty/tampered signature, a tampered field, a foreign key, or any
    /// crypto fault → Fail(UPDATE_SIG_INVALID). Never throws, never Ok by default. The
    /// caller must not download the payload unless this returns Ok.</summary>
    Result VerifyManifest(UpdateManifest manifest);

    /// <summary>Step 2 — verify the DOWNLOADED payload against the (already
    /// signature-verified) manifest. Computes SHA-256 of <paramref name="payload"/> and
    /// compares to <paramref name="manifest"/>.Sha256. Also enforces the no-downgrade
    /// rule against <paramref name="installedVersion"/> (skipped only when
    /// <paramref name="allowDowngrade"/> is true, e.g. a beta channel). Hash mismatch →
    /// Fail(UPDATE_HASH_MISMATCH) (payload never executed); lower version →
    /// Fail(UPDATE_DOWNGRADE). Never throws.</summary>
    Result VerifyPayload(
        UpdateManifest manifest, byte[] payload, string installedVersion, bool allowDowngrade);
}

/// <summary>The update feed seam — fetches the signed manifest and the payload bytes
/// for a channel over the INJECTED transport. (19/09/2026 : ce texte disait « PINNED » ;
/// l'epinglage du serveur de mise a jour n'existe pas encore.) A fake stands in for tests. No
/// network → Fail(UPDATE_OFFLINE); the app keeps running on the current version
/// (updates are never blocking).</summary>
public interface IUpdateFeed
{
    /// <summary>Fetches the latest signed manifest for <paramref name="channel"/>.
    /// Offline / transport failure → Fail(UPDATE_OFFLINE). Ok(null) when the feed is
    /// reachable but offers no manifest (already current).</summary>
    Task<Result<UpdateManifest?>> GetManifestAsync(UpdateChannel channel, CancellationToken ct);

    /// <summary>Downloads the payload at <paramref name="url"/> (the url comes only from
    /// an already signature-verified manifest). Offline / transport failure →
    /// Fail(UPDATE_OFFLINE).</summary>
    Task<Result<byte[]>> DownloadAsync(string url, CancellationToken ct);
}

/// <summary>Minimal filesystem seam so the profiles channel's atomic extraction is
/// fully testable with an in-memory fake (no real disk, no admin). Every member is
/// Result-wrapped — IO failure never throws across the boundary.</summary>
public interface IUpdateFileSystem
{
    /// <summary>True iff the directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Les octets d'un fichier, ou <c>null</c> s'il est absent ou illisible. Sert a ne pas
    /// retelecharger une charge deja stagee (elle est de toute facon RE-VERIFIEE). Par defaut : null.</summary>
    byte[]? TryReadFile(string path) => null;

    /// <summary>Creates the directory (and parents). IO failure → Fail.</summary>
    Result CreateDirectory(string path);

    /// <summary>Writes bytes to a file, overwriting. IO failure → Fail.</summary>
    Result WriteFile(string path, byte[] bytes);

    /// <summary>Atomically swaps <paramref name="stagingDir"/> into
    /// <paramref name="targetDir"/> (the live pack), keeping the OLD pack intact if the
    /// swap can't complete (clean transaction — never a half-applied state). IO failure
    /// → Fail; on failure the previous target is left untouched.</summary>
    Result ReplaceDirectory(string stagingDir, string targetDir);

    /// <summary>Best-effort delete of a staging dir (cleanup). Never throws; a failure
    /// to clean leftover staging is non-fatal and returns Fail for the caller to log.</summary>
    Result DeleteDirectory(string path);
}

/// <summary>The TYPED, allow-listed elevated seam that replaces signed binaries in
/// Program Files (T13 / §9.1). It is not an arbitrary write primitive: the only
/// operation it exposes is "replace these already-verified, staged files". For M5 the
/// live elevation + helper wiring is DEFERRED (needs UAC) — the default impl is
/// fail-clean (UPDATE_REPLACER_UNAVAILABLE), exactly like the M2 deferred seams. A
/// verification failure at ANY earlier step means this is never even called.</summary>
public interface IElevatedFileReplacer
{
    /// <summary>Replaces the live app files from a verified staged payload. Only invoked
    /// after the manifest signature AND the payload hash AND the no-downgrade rule all
    /// passed and the payload was staged atomically. Deferred impl → Fail(
    /// UPDATE_REPLACER_UNAVAILABLE) (nothing replaced); live impl performs the typed swap.</summary>
    Task<Result> ReplaceAppFilesAsync(string stagedPayloadPath, string targetInstallDir, CancellationToken ct);
}
