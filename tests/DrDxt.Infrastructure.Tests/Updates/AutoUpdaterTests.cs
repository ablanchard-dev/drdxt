using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;
using NSec.Cryptography;

namespace DrDxt.Infrastructure.Tests.Updates;

// -----------------------------------------------------------------------
// T13 — Two-channel signed auto-updater. Matrix (verbatim from the plan):
//
//   Profiles channel (real, no admin):
//     * new pack → verify manifest (T12) + hash → atomic extract into profiles dir
//     * corrupt pack → OLD pack CONSERVED (clean transaction)
//   App channel:
//     * verify → download → verify hash → stage atomically → delegate Program Files
//       swap to the typed IElevatedFileReplacer seam (deferred fail-clean in M5)
//     * verification fails at ANY step → NO file replaced (no half-applied state)
//   No network → Fail(UPDATE_OFFLINE), app keeps running (never blocking)
//   Imposed order: manifest signature verified before the payload is even downloaded.
// -----------------------------------------------------------------------
public sealed class AutoUpdaterTests
{
    private const string Installed = "1.2.0";
    private const string ProfilesDir = @"X:\local\DrDXT\profiles";
    private const string StagingRoot = @"X:\local\DrDXT\staging";
    private const string AppInstallDir = @"C:\Program Files\DrDXT";

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static IUpdateVerifier RealVerifier()
        => UpdateDevKeys.CreateUpdateVerifier().Value!;

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static UpdateManifest SignedManifest(
        UpdateChannel channel, string version, string url, byte[] payload)
    {
        var unsigned = new UpdateManifest(channel, version, url, Sha256Hex(payload), Signature: string.Empty);
        using var key = Key.Import(
            SignatureAlgorithm.Ed25519, UpdateDevKeys.DevPrivateSeed, KeyBlobFormat.RawPrivateKey);
        var sig = SignatureAlgorithm.Ed25519.Sign(key, UpdateVerifier.CanonicalBytes(unsigned));
        return unsigned with { Signature = Convert.ToBase64String(sig) };
    }

    private static AutoUpdater Build(
        IUpdateFeed feed,
        IUpdateVerifier? verifier = null,
        IUpdateFileSystem? fs = null,
        IElevatedFileReplacer? replacer = null,
        string installed = Installed)
        => new(
            feed,
            verifier ?? RealVerifier(),
            fs ?? new InMemoryFs(),
            replacer ?? new DeferredElevatedFileReplacer(),
            ProfilesDir, StagingRoot, AppInstallDir, installed);

    // ── Canal ──────────────────────────────────────────────────────────────────

    /// <summary>Revue du 09/10/2026 : un manifeste signé pour les profils, servi sur le chemin de l'App, passait la signature
    /// et l'empreinte. Le canal est dans les octets signés : il doit être celui demandé, sinon refus avant tout téléchargement.</summary>
    [Fact]
    public async Task Un_manifeste_d_un_autre_canal_est_refuse_avant_tout_telechargement()
    {
        var payload = MakeZip(("readme.txt", "pack"));
        var manifest = SignedManifest(UpdateChannel.Profiles, "9.9.9", "https://u/profiles.zip", payload);
        var feed = new FakeFeed(manifest, payload);

        var result = await Build(feed).UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
        feed.DownloadCount.Should().Be(0, "rien n'est téléchargé sur un manifeste du mauvais canal");
    }

    // ── Profiles channel ────────────────────────────────────────────────────────

    [Fact]
    public async Task Profiles_NewSignedPack_VerifiesAndExtractsAtomically()
    {
        var payload = MakeZip(("readme.txt", "v1.3 pack"), ("packs/cod.json", "{\"fps\":1}"));
        var manifest = SignedManifest(UpdateChannel.Profiles, "1.3.0", "https://u/profiles.zip", payload);
        var fs = new InMemoryFs();

        var updater = Build(new FakeFeed(manifest, payload), fs: fs);

        var result = await updater.UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value.Should().Be(UpdateOutcome.ProfilesInstalled);
        // The live profiles dir now exists with the pack's files (committed via atomic swap).
        fs.DirectoryExists(ProfilesDir).Should().BeTrue();
        fs.FileContent(Path.Combine(ProfilesDir, "readme.txt")).Should().Be("v1.3 pack");
        fs.FileContent(Path.Combine(ProfilesDir, "packs", "cod.json")).Should().Be("{\"fps\":1}");
        // Staging was cleaned up.
        fs.CommittedDirs.Should().Contain(ProfilesDir);
    }

    [Fact]
    public async Task Profiles_CorruptPack_OldPackConserved()
    {
        // The manifest is honestly signed and the hash matches, but the bytes are not a
        // valid zip → extraction fails → the live profiles dir must be left untouched.
        var notAZip = Encoding.UTF8.GetBytes("this is not a zip archive");
        var manifest = SignedManifest(UpdateChannel.Profiles, "1.3.0", "https://u/profiles.zip", notAZip);

        var fs = new InMemoryFs();
        fs.SeedDirectory(ProfilesDir);
        fs.SeedFile(Path.Combine(ProfilesDir, "old.json"), "OLD-PACK"); // the previous pack

        var updater = Build(new FakeFeed(manifest, notAZip), fs: fs);

        var result = await updater.UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.StageFailed);
        // Old pack intact — the swap never happened.
        fs.FileContent(Path.Combine(ProfilesDir, "old.json")).Should().Be("OLD-PACK");
        fs.ReplaceCalls.Should().Be(0, "a corrupt pack must never reach the atomic swap");
    }

    [Fact]
    public async Task Profiles_HashMismatch_NotInstalled_OldPackConserved()
    {
        // Manifest signed for ONE payload; the feed serves a DIFFERENT one (CDN swap).
        var realPayload = MakeZip(("a.txt", "real"));
        var manifest = SignedManifest(UpdateChannel.Profiles, "1.3.0", "https://u/profiles.zip", realPayload);
        var tampered = MakeZip(("a.txt", "tampered"));

        var fs = new InMemoryFs();
        fs.SeedDirectory(ProfilesDir);
        fs.SeedFile(Path.Combine(ProfilesDir, "old.json"), "OLD");

        var updater = Build(new FakeFeed(manifest, tampered), fs: fs);

        var result = await updater.UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.HashMismatch);
        fs.FileContent(Path.Combine(ProfilesDir, "old.json")).Should().Be("OLD");
        fs.WriteCalls.Should().Be(0, "a hash-mismatched payload is never even staged");
    }

    [Fact]
    public async Task Profiles_UnsignedManifest_DownloadNeverHappens()
    {
        var payload = MakeZip(("a.txt", "x"));
        var unsigned = new UpdateManifest(
            UpdateChannel.Profiles, "1.3.0", "https://u/profiles.zip", Sha256Hex(payload), Signature: "");
        var feed = new FakeFeed(unsigned, payload);

        var result = await Build(feed).UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
        feed.DownloadCount.Should().Be(0,
            "an untrusted manifest must not trigger a payload download (signature before payload)");
    }

    [Fact]
    public async Task Profiles_NoManifestOffered_IsUpToDate_NoOp()
    {
        var fs = new InMemoryFs();
        var result = await Build(new FakeFeed(manifest: null, payload: null), fs: fs)
            .UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value.Should().Be(UpdateOutcome.UpToDate);
        fs.WriteCalls.Should().Be(0);
        fs.ReplaceCalls.Should().Be(0);
    }

    [Fact]
    public async Task Profiles_Offline_FailsCleanly_NonBlocking()
    {
        var result = await Build(FakeFeed.Offline()).UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.Offline);
    }

    [Fact]
    public async Task Profiles_ZipSlipEntry_Rejected_NothingWrittenOutsideStaging()
    {
        // A malicious pack tries to escape the staging dir with a traversal path.
        var evil = MakeZip(("../../escape.txt", "pwned"));
        var manifest = SignedManifest(UpdateChannel.Profiles, "1.3.0", "https://u/profiles.zip", evil);
        var fs = new InMemoryFs();

        var result = await Build(new FakeFeed(manifest, evil), fs: fs).UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.StageFailed);
        fs.WrittenPaths.Should().NotContain(p => p.Contains("escape.txt"));
        fs.ReplaceCalls.Should().Be(0);
    }

    // Audit securite 26/08 : le zip-slip par chemin ABSOLU est un vecteur distinct du
    // traversal `..` (deja teste ci-dessus). Path.Combine(staging, "C:\...") RETOURNE
    // l'absolu et sort du sandbox ; SafeCombine doit le refuser (StartsWith echoue).
    // Non teste avant ce commit.
    [Fact]
    public async Task Profiles_ZipSlipAbsolutePath_Rejected_NothingWrittenOutsideStaging()
    {
        var evil = MakeZip((@"C:\Windows\System32\drdxt_pwned.dll", "pwned"));
        var manifest = SignedManifest(UpdateChannel.Profiles, "1.3.0", "https://u/profiles.zip", evil);
        var fs = new InMemoryFs();

        var result = await Build(new FakeFeed(manifest, evil), fs: fs).UpdateProfilesAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.StageFailed);
        fs.WrittenPaths.Should().NotContain(p => p.Contains("System32"));
        fs.ReplaceCalls.Should().Be(0);
    }

    // ── App channel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task App_VerifiedPayload_Staged_ThenDeferredReplacer_NoHalfApply()
    {
        var payload = Encoding.UTF8.GetBytes("the-new-app-binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/app.pkg", payload);
        var fs = new InMemoryFs();
        var replacer = new RecordingReplacer(new DeferredElevatedFileReplacer());

        var result = await Build(new FakeFeed(manifest, payload), fs: fs, replacer: replacer)
            .UpdateAppAsync(CancellationToken.None);

        // Deferred replacer → fail-clean, but the payload WAS staged first.
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.ReplacerUnavailable);
        fs.WriteCalls.Should().BeGreaterThan(0, "the verified payload is staged before delegating");
        replacer.Calls.Should().Be(1, "staging succeeded, so the typed replacer IS invoked");
        replacer.LastStagedPath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task App_SignatureInvalid_NoStaging_NoReplace()
    {
        var payload = Encoding.UTF8.GetBytes("binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/app.pkg", payload);
        var tampered = manifest with { Url = "https://evil/app.pkg" }; // breaks the signature
        var fs = new InMemoryFs();
        var replacer = new RecordingReplacer(new DeferredElevatedFileReplacer());

        var result = await Build(new FakeFeed(tampered, payload), fs: fs, replacer: replacer)
            .UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
        fs.WriteCalls.Should().Be(0, "nothing is staged when the manifest signature fails");
        replacer.Calls.Should().Be(0, "the replacer is never reached on a verification failure");
    }

    [Fact]
    public async Task App_HashMismatch_NoStaging_NoReplace()
    {
        var manifest = SignedManifest(
            UpdateChannel.App, "1.3.0", "https://u/app.pkg", Encoding.UTF8.GetBytes("real"));
        var tampered = Encoding.UTF8.GetBytes("tampered-binary");
        var fs = new InMemoryFs();
        var replacer = new RecordingReplacer(new DeferredElevatedFileReplacer());

        var result = await Build(new FakeFeed(manifest, tampered), fs: fs, replacer: replacer)
            .UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.HashMismatch);
        fs.WriteCalls.Should().Be(0);
        replacer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task App_Downgrade_Refused_NoStaging_NoReplace()
    {
        var payload = Encoding.UTF8.GetBytes("older-binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.1.0", "https://u/app.pkg", payload);
        var fs = new InMemoryFs();
        var replacer = new RecordingReplacer(new DeferredElevatedFileReplacer());

        var result = await Build(new FakeFeed(manifest, payload), fs: fs, replacer: replacer)
            .UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.Downgrade);
        fs.WriteCalls.Should().Be(0);
        replacer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task App_Offline_FailsCleanly_NonBlocking_NoReplace()
    {
        var replacer = new RecordingReplacer(new DeferredElevatedFileReplacer());
        var result = await Build(FakeFeed.Offline(), replacer: replacer).UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.Offline);
        replacer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task App_LiveReplacerSucceeds_ReturnsAppStaged()
    {
        // Prove the success path once a live replacer exists (here a fake Ok replacer).
        var payload = Encoding.UTF8.GetBytes("the-new-app-binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/app.pkg", payload);
        var replacer = new RecordingReplacer(new OkReplacer());

        var result = await Build(new FakeFeed(manifest, payload), replacer: replacer)
            .UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value.Should().Be(UpdateOutcome.AppStaged);
        replacer.Calls.Should().Be(1);
        replacer.LastTargetDir.Should().Be(AppInstallDir);
    }

    // ── Revue du 10/09/2026 : la charge stagee n'est pas retelechargee a chaque demarrage ──

    [Fact]
    public async Task App_a_payload_already_staged_with_the_signed_hash_is_not_downloaded_again()
    {
        var payload = Encoding.UTF8.GetBytes("installeur signe 1.3.0");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", payload);
        var feed = new FakeFeed(manifest, payload);
        var updater = Build(feed, fs: new InMemoryFs());

        await updater.UpdateAppAsync(CancellationToken.None);
        var second = await updater.UpdateAppAsync(CancellationToken.None);

        feed.DownloadCount.Should().Be(1, "la charge stagee au premier passage est reprise, puis re-verifiee");
        second.Error!.Value.Code.Should().Be(UpdateErrorCodes.ReplacerUnavailable, "elle reste une version verifiee");
        updater.DerniereVersionApp!.Version.Should().Be("1.3.0");
    }

    [Fact]
    public async Task App_a_staged_payload_that_no_longer_matches_is_downloaded_again()
    {
        var payload = Encoding.UTF8.GetBytes("installeur signe 1.3.0");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", payload);
        var feed = new FakeFeed(manifest, payload);
        var fs = new InMemoryFs();
        var updater = Build(feed, fs: fs);

        await updater.UpdateAppAsync(CancellationToken.None);
        fs.SeedFile(updater.StagedAppPayloadPath, "trafique sur le disque");
        await updater.UpdateAppAsync(CancellationToken.None);

        feed.DownloadCount.Should().Be(2, "une charge stagee qui ne correspond plus au manifeste signe n'est jamais reprise");
    }

    // ── fakes ─────────────────────────────────────────────────────────────────────

    private sealed class FakeFeed : IUpdateFeed
    {
        private readonly UpdateManifest? _manifest;
        private readonly byte[]? _payload;
        private readonly bool _offline;
        public int DownloadCount { get; private set; }

        public FakeFeed(UpdateManifest? manifest, byte[]? payload)
        {
            _manifest = manifest;
            _payload = payload;
        }

        private FakeFeed(bool offline) => _offline = offline;

        public static FakeFeed Offline() => new(offline: true);

        public Task<Result<UpdateManifest?>> GetManifestAsync(UpdateChannel channel, CancellationToken ct)
            => Task.FromResult(_offline
                ? Result<UpdateManifest?>.Fail(UpdateErrorCodes.Offline, "no network")
                : Result<UpdateManifest?>.Ok(_manifest));

        public Task<Result<byte[]>> DownloadAsync(string url, CancellationToken ct)
        {
            DownloadCount++;
            if (_offline)
                return Task.FromResult(Result<byte[]>.Fail(UpdateErrorCodes.Offline, "no network"));
            return Task.FromResult(Result<byte[]>.Ok(_payload ?? Array.Empty<byte>()));
        }
    }

    private sealed class RecordingReplacer : IElevatedFileReplacer
    {
        private readonly IElevatedFileReplacer _inner;
        public int Calls { get; private set; }
        public string? LastStagedPath { get; private set; }
        public string? LastTargetDir { get; private set; }

        public RecordingReplacer(IElevatedFileReplacer inner) => _inner = inner;

        public Task<Result> ReplaceAppFilesAsync(string stagedPayloadPath, string targetInstallDir, CancellationToken ct)
        {
            Calls++;
            LastStagedPath = stagedPayloadPath;
            LastTargetDir = targetInstallDir;
            return _inner.ReplaceAppFilesAsync(stagedPayloadPath, targetInstallDir, ct);
        }
    }

    private sealed class OkReplacer : IElevatedFileReplacer
    {
        public Task<Result> ReplaceAppFilesAsync(string stagedPayloadPath, string targetInstallDir, CancellationToken ct)
            => Task.FromResult(Result.Ok());
    }

    // In-memory filesystem fake — records writes/replaces and models the atomic swap so a
    // failed/corrupt extraction leaves the previously-committed live dir intact.
    private sealed class InMemoryFs : IUpdateFileSystem
    {
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public int WriteCalls { get; private set; }
        public int ReplaceCalls { get; private set; }
        public List<string> WrittenPaths { get; } = new();
        public HashSet<string> CommittedDirs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SeedDirectory(string path) => _dirs.Add(Norm(path));
        public void SeedFile(string path, string content)
        {
            _files[Norm(path)] = Encoding.UTF8.GetBytes(content);
            _dirs.Add(Norm(Path.GetDirectoryName(path)!));
        }

        public string? FileContent(string path)
            => _files.TryGetValue(Norm(path), out var b) ? Encoding.UTF8.GetString(b) : null;

        public bool DirectoryExists(string path) => _dirs.Contains(Norm(path));

        public byte[]? TryReadFile(string path) => _files.TryGetValue(Norm(path), out var b) ? b : null;

        public Result CreateDirectory(string path)
        {
            _dirs.Add(Norm(path));
            return Result.Ok();
        }

        public Result WriteFile(string path, byte[] bytes)
        {
            WriteCalls++;
            WrittenPaths.Add(Norm(path));
            _files[Norm(path)] = bytes;
            _dirs.Add(Norm(Path.GetDirectoryName(path)!));
            return Result.Ok();
        }

        public Result ReplaceDirectory(string stagingDir, string targetDir)
        {
            ReplaceCalls++;
            // Atomic swap: drop the old target's files, move staging's files under target.
            RemoveTree(targetDir);
            var sNorm = Norm(stagingDir);
            foreach (var key in _files.Keys.Where(k => k.StartsWith(sNorm, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var rel = key.Substring(sNorm.Length).TrimStart('\\', '/');
                var moved = Norm(Path.Combine(targetDir, rel));
                _files[moved] = _files[key];
                _files.Remove(key);
                _dirs.Add(Norm(Path.GetDirectoryName(moved)!));
            }
            RemoveTree(stagingDir);
            _dirs.Add(Norm(targetDir));
            CommittedDirs.Add(Norm(targetDir));
            return Result.Ok();
        }

        public Result DeleteDirectory(string path)
        {
            RemoveTree(path);
            return Result.Ok();
        }

        private void RemoveTree(string path)
        {
            var n = Norm(path);
            _dirs.RemoveWhere(d => d.Equals(n, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(n + "\\", StringComparison.OrdinalIgnoreCase));
            foreach (var k in _files.Keys.Where(k => k.StartsWith(n + "\\", StringComparison.OrdinalIgnoreCase)
                || k.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList())
                _files.Remove(k);
        }

        private static string Norm(string p) => Path.GetFullPath(p);
    }
}
