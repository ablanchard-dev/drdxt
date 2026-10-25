using System.Security.Cryptography;
using System.Text;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;
using NSec.Cryptography;
using DrDxt.TestSupport;

namespace DrDxt.Infrastructure.Tests.Updates;

// -----------------------------------------------------------------------
// Mise à jour signée de l'application :
//     * annoncer (le manifeste seul, signature vérifiée) ; au clic : télécharger → vérifier l'empreinte → préparer
//     * la vérification échoue à n'importe quelle étape → rien n'est préparé
//   Sans réseau → Fail(UPDATE_OFFLINE), l'application continue (jamais bloquante)
//   Ordre imposé : signature du manifeste vérifiée avant même de télécharger la charge.
// -----------------------------------------------------------------------
public sealed class AutoUpdaterTests
{
    private const string Installed = "1.2.0";
    private const string StagingRoot = @"X:\local\DrDXT\staging";

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
            SignatureAlgorithm.Ed25519, UpdateDevTestKeys.DevPrivateSeed, KeyBlobFormat.RawPrivateKey);
        var sig = SignatureAlgorithm.Ed25519.Sign(key, UpdateVerifier.CanonicalBytes(unsigned));
        return unsigned with { Signature = Convert.ToBase64String(sig) };
    }

    private static AutoUpdater Build(
        IUpdateFeed feed,
        IUpdateVerifier? verifier = null,
        IUpdateFileSystem? fs = null,
        string installed = Installed)
        => new(
            feed,
            verifier ?? RealVerifier(),
            fs ?? new InMemoryFs(),
            StagingRoot, installed);

    // ── Canal ──────────────────────────────────────────────────────────────────

    /// <summary>Un manifeste signé pour un autre canal, servi sur le chemin de l'App, passerait la signature
    /// et l'empreinte. Le canal est dans les octets signés : il doit être celui demandé, sinon refus avant tout téléchargement.</summary>
    [Fact]
    public async Task Un_manifeste_d_un_autre_canal_est_refuse_avant_tout_telechargement()
    {
        var payload = Payload("pack");
        var manifest = SignedManifest((UpdateChannel)7, "9.9.9", "https://u/other.zip", payload);
        var feed = new FakeFeed(manifest, payload);

        var result = await Build(feed).AnnoncerAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
        feed.DownloadCount.Should().Be(0, "rien n'est téléchargé sur un manifeste du mauvais canal");
    }

    [Fact]
    public async Task Annoncer_un_manifeste_mal_signe_ne_telecharge_ni_ne_prepare_rien()
    {
        var payload = Encoding.UTF8.GetBytes("binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/app.pkg", payload);
        var tampered = manifest with { Url = "https://evil/app.pkg" }; // casse la signature
        var fs = new InMemoryFs();
        var feed = new FakeFeed(tampered, payload);

        var result = await Build(feed, fs: fs).AnnoncerAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
        feed.DownloadCount.Should().Be(0);
        fs.WriteCalls.Should().Be(0, "nothing is staged when the manifest signature fails");
    }

    // Le manifeste a fait l'aller-retour par l'écran : une version plus ancienne, même signée, n'est jamais préparée.
    [Fact]
    public async Task Au_clic_une_version_plus_ancienne_n_est_jamais_preparee()
    {
        var payload = Encoding.UTF8.GetBytes("older-binary");
        var manifest = SignedManifest(UpdateChannel.App, "1.1.0", "https://u/app.pkg", payload);
        var fs = new InMemoryFs();

        var result = await Build(new FakeFeed(manifest, payload), fs: fs).TelechargerAsync(manifest, CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.Downgrade);
        fs.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Annoncer_hors_ligne_echoue_proprement_sans_bloquer()
    {
        var result = await Build(FakeFeed.Offline()).AnnoncerAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.Offline);
    }

    // ── La charge stagee n'est pas retelechargee a chaque demarrage ──

    [Fact]
    public async Task App_a_payload_already_staged_with_the_signed_hash_is_not_downloaded_again()
    {
        var payload = Encoding.UTF8.GetBytes("installeur signe 1.3.0");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", payload);
        var feed = new FakeFeed(manifest, payload);
        var updater = Build(feed, fs: new InMemoryFs());

        await updater.TelechargerAsync(manifest, CancellationToken.None);
        var second = await updater.TelechargerAsync(manifest, CancellationToken.None);

        feed.DownloadCount.Should().Be(1, "la charge stagee au premier passage est reprise, puis re-verifiee");
        second.Value.Should().Be(updater.StagedAppPayloadPath, "elle reste une version verifiee");
    }

    [Fact]
    public async Task App_a_staged_payload_that_no_longer_matches_is_downloaded_again()
    {
        var payload = Encoding.UTF8.GetBytes("installeur signe 1.3.0");
        var manifest = SignedManifest(UpdateChannel.App, "1.3.0", "https://u/DrDXT-Setup.exe", payload);
        var feed = new FakeFeed(manifest, payload);
        var fs = new InMemoryFs();
        var updater = Build(feed, fs: fs);

        await updater.TelechargerAsync(manifest, CancellationToken.None);
        fs.SeedFile(updater.StagedAppPayloadPath, "trafique sur le disque");
        await updater.TelechargerAsync(manifest, CancellationToken.None);

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

    // Une exception que les coutures ne prévoient pas ne traverse jamais la frontière : la mise à jour échoue fermée,
    // rien n'est proposé ni téléchargé.
    private sealed class FluxQuiLeve : IUpdateFeed
    {
        public Task<Result<UpdateManifest?>> GetManifestAsync(UpdateChannel channel, CancellationToken ct)
            => throw new InvalidOperationException("imprévu");
        public Task<Result<byte[]>> DownloadAsync(string url, CancellationToken ct)
            => throw new InvalidOperationException("imprévu");
    }

    [Fact]
    public async Task Une_exception_imprevue_rend_un_echec_ferme_et_jamais_un_lancer()
    {
        var annonce = await Build(new FluxQuiLeve()).AnnoncerAsync(CancellationToken.None);
        annonce.Error!.Value.Code.Should().Be(UpdateErrorCodes.Unexpected);

        var manifeste = SignedManifest(UpdateChannel.App, "9.9.9", "https://exemple.test/x.exe", Payload("x"));
        var telechargement = await Build(new FluxQuiLeve()).TelechargerAsync(manifeste, CancellationToken.None);
        telechargement.Error!.Value.Code.Should().Be(UpdateErrorCodes.Unexpected);
    }

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
