using System.Security.Cryptography;
using System.Text;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;
using NSec.Cryptography;

namespace DrDxt.Infrastructure.Tests.Updates;

// -----------------------------------------------------------------------
// Mise à jour signée de l'application :
//     * vérifier → télécharger → vérifier l'empreinte → préparer atomiquement → déléguer
//       l'échange dans Program Files à la couture typée IElevatedFileReplacer (différée, échoue proprement)
//     * la vérification échoue à n'importe quelle étape → aucun fichier remplacé (aucun état à moitié appliqué)
//   Sans réseau → Fail(UPDATE_OFFLINE), l'application continue (jamais bloquante)
//   Ordre imposé : signature du manifeste vérifiée avant même de télécharger la charge.
// -----------------------------------------------------------------------
public sealed class AutoUpdaterTests
{
    private const string Installed = "1.2.0";
    private const string StagingRoot = @"X:\local\DrDXT\staging";
    private const string AppInstallDir = @"C:\Program Files\DrDXT";

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static IUpdateVerifier RealVerifier()
        => UpdateDevKeys.CreateUpdateVerifier().Value!;

    private static byte[] Payload(string content) => Encoding.UTF8.GetBytes(content);

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
            StagingRoot, AppInstallDir, installed);

    // ── Canal ──────────────────────────────────────────────────────────────────

    /// <summary>Un manifeste signé pour un autre canal, servi sur le chemin de l'App, passerait la signature
    /// et l'empreinte. Le canal est dans les octets signés : il doit être celui demandé, sinon refus avant tout téléchargement.</summary>
    [Fact]
    public async Task Un_manifeste_d_un_autre_canal_est_refuse_avant_tout_telechargement()
    {
        var payload = Payload("pack");
        var manifest = SignedManifest((UpdateChannel)7, "9.9.9", "https://u/other.zip", payload);
        var feed = new FakeFeed(manifest, payload);

        var result = await Build(feed).UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
        feed.DownloadCount.Should().Be(0, "rien n'est téléchargé sur un manifeste du mauvais canal");
    }

    // ── Canal App ────────────────────────────────────────────────────────────────

    // L'échange suit le téléchargement : il reçoit sa borne (des minutes), pas celle du manifeste (secondes), déjà passée
    // après un long téléchargement.
    [Fact]
    public async Task App_Replacer_Gets_The_Download_Budget_Not_The_Manifest_One()
    {
        var payload = Encoding.UTF8.GetBytes("the-new-app-binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/app.pkg", payload);
        var replacer = new RecordingReplacer(new DeferredElevatedFileReplacer());
        using var manifeste = new CancellationTokenSource();
        using var telechargement = new CancellationTokenSource();

        await Build(new FakeFeed(manifest, payload), fs: new InMemoryFs(), replacer: replacer)
            .UpdateAppAsync(manifeste.Token, telechargement.Token);

        replacer.LastToken.Should().Be(telechargement.Token);
    }

    [Fact]
    public async Task App_VerifiedPayload_Staged_ThenDeferredReplacer_NoHalfApply()
    {
        var payload = Encoding.UTF8.GetBytes("the-new-app-binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/app.pkg", payload);
        var fs = new InMemoryFs();
        var replacer = new RecordingReplacer(new DeferredElevatedFileReplacer());

        var result = await Build(new FakeFeed(manifest, payload), fs: fs, replacer: replacer)
            .UpdateAppAsync(CancellationToken.None);

        // Remplaceur différé → échec propre, mais la charge a bien été préparée d'abord.
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
        var tampered = manifest with { Url = "https://evil/app.pkg" }; // casse la signature
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
        // Prouver le chemin de succès une fois un remplaceur vivant présent (ici un faux remplaceur Ok).
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

    [Fact]
    public async Task App_the_installed_version_is_up_to_date_not_a_new_version()
    {
        var payload = Encoding.UTF8.GetBytes("installeur deja en place");
        var manifest = SignedManifest(UpdateChannel.App, Installed, "https://u/app.pkg", payload);
        var replacer = new RecordingReplacer(new OkReplacer());

        var result = await Build(new FakeFeed(manifest, payload), replacer: replacer).UpdateAppAsync(CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value.Should().Be(UpdateOutcome.UpToDate);
        replacer.Calls.Should().Be(0);
    }

    // ── La charge stagee n'est pas retelechargee a chaque demarrage ──

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

    // ── L'installeur ne part qu'au clic ──
    // Au démarrage, l'App ne doit lire que le manifeste signé (quelques centaines d'octets) : l'installeur (jusqu'à 400 Mo)
    // partait sans clic, alors que le README et MISE-A-JOUR.md disent l'inverse.

    [Fact]
    public async Task Annoncer_dit_la_version_signee_sans_rien_telecharger()
    {
        var payload = Payload("installeur signe 1.3.0");
        var feed = new FakeFeed(SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", payload), payload);
        var fs = new InMemoryFs();

        var annonce = await Build(feed, fs: fs).AnnoncerAsync(CancellationToken.None);

        annonce.Value!.Version.Should().Be("1.3.0");
        feed.DownloadCount.Should().Be(0, "rien ne se télécharge avant le clic");
        fs.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Annoncer_ne_propose_ni_la_version_installee_ni_une_plus_ancienne()
    {
        var payload = Payload("x");
        (await Build(new FakeFeed(SignedManifest(UpdateChannel.App, Installed, "https://u/a.exe", payload), payload))
            .AnnoncerAsync(CancellationToken.None)).Value.Should().BeNull();
        (await Build(new FakeFeed(SignedManifest(UpdateChannel.App, "1.1.0", "https://u/a.exe", payload), payload))
            .AnnoncerAsync(CancellationToken.None)).Error!.Value.Code.Should().Be(UpdateErrorCodes.Downgrade);
    }

    [Fact]
    public async Task Au_clic_la_charge_est_telechargee_verifiee_puis_stagee()
    {
        var payload = Payload("installeur signe 1.3.0");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", payload);
        var feed = new FakeFeed(manifest, payload);
        var fs = new InMemoryFs();
        var updater = Build(feed, fs: fs);

        var chemin = await updater.TelechargerAsync(manifest, CancellationToken.None);

        chemin.Value.Should().Be(updater.StagedAppPayloadPath);
        fs.TryReadFile(chemin.Value!).Should().Equal(payload);
        feed.DownloadCount.Should().Be(1);
    }

    [Fact]
    public async Task Au_clic_une_charge_trafiquee_n_est_jamais_stagee()
    {
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", Payload("vrai"));
        var fs = new InMemoryFs();

        var r = await Build(new FakeFeed(manifest, Payload("trafique")), fs: fs).TelechargerAsync(manifest, CancellationToken.None);

        r.Error!.Value.Code.Should().Be(UpdateErrorCodes.HashMismatch);
        fs.WriteCalls.Should().Be(0);
    }

    // Le manifeste a fait l'aller-retour par l'écran : il est relu signé avant tout téléchargement.
    [Fact]
    public async Task Au_clic_un_manifeste_modifie_ne_telecharge_rien()
    {
        var payload = Payload("vrai");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", payload);
        var feed = new FakeFeed(manifest, payload);

        var r = await Build(feed).TelechargerAsync(manifest with { Url = "https://evil/x.exe" }, CancellationToken.None);

        r.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
        feed.DownloadCount.Should().Be(0);
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
        public CancellationToken LastToken { get; private set; }

        public RecordingReplacer(IElevatedFileReplacer inner) => _inner = inner;

        public Task<Result> ReplaceAppFilesAsync(string stagedPayloadPath, string targetInstallDir, CancellationToken ct)
        {
            Calls++;
            LastStagedPath = stagedPayloadPath;
            LastTargetDir = targetInstallDir;
            LastToken = ct;
            return _inner.ReplaceAppFilesAsync(stagedPayloadPath, targetInstallDir, ct);
        }
    }

    private sealed class OkReplacer : IElevatedFileReplacer
    {
        public Task<Result> ReplaceAppFilesAsync(string stagedPayloadPath, string targetInstallDir, CancellationToken ct)
            => Task.FromResult(Result.Ok());
    }

    // Faux système de fichiers en mémoire : enregistre écritures et remplacements et modélise l'échange
    // atomique, pour qu'une extraction ratée ou corrompue laisse intact le dossier vivant déjà commité.
    private sealed class InMemoryFs : IUpdateFileSystem
    {
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public int WriteCalls { get; private set; }
        public int ReplaceCalls { get; private set; }
        public List<string> WrittenPaths { get; } = new();

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
