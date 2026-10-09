using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using DrDxt.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Composition smoke: the production DI graph resolves end-to-end, and the manifest
// store is VERIFY-only (it can never sign on the host).
// -----------------------------------------------------------------------
public sealed class HostCompositionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDrDxtReconcilerHost();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    // A provider whose manifest storage is an injected IN-MEMORY seam (pre-registered so
    // the host's TryAddSingleton does not override it). This lets the manifest verify
    // tests round-trip without touching the real %ProgramData% / admin ACL.
    [Fact]
    public async Task The_boot_reconciler_can_NEVER_reboot_the_machine_into_the_BIOS()
    {
        // M3 lot 2. Cet hote tourne sans SURVEILLANCE. Un redemarrage qu'il aurait declenche
        // arriverait chez le client comme une machine qui repart dans le BIOS toute seule,
        // sans rien a l'ecran pour l'expliquer -- et un reboot sans cause visible detruit la
        // confiance plus surement que n'importe quel bug. La couture firmware n'est donc
        // deliberement pas passee ici, exactement comme les coutures GPU juste au-dessus.
        //
        // Ce test epingle la decision de composition, pas seulement le comportement de
        // l'executeur : il resout le vrai graphe de production et lui envoie l'op.
        using var sp = BuildProvider();
        var client = sp.GetRequiredService<IElevatedClient>();

        var r = await client.ExecuteAsync(new RebootToFirmwareSetupOp(0), default);

        r.IsOk.Should().BeFalse();
        // Et l'echec est DIT : cabler la couture ici ferait passer ce test au vert, donc
        // quiconque la cable devra lire ce commentaire et assumer le changement.
        r.Error!.Value.Message.Should().Contain("not wired");
    }

    // 11/09 — le jumeau GPU du test CI-dessus, et il manquait.
    //
    // Rien ne prouvait que l'hote de boot refuse d'ecrire le GPU : la composition cable bien
    // `DisabledNvApiActions` / `DisabledAdlxActions`, mais aucun test ne le tenait, et le
    // commentaire de ces deux coutures ANNONCAIT une seconde barriere qui n'existe pas —
    // `OpAllowlist.ValidateAdlx` accepte un `ApplyAdlxTuningOp` bien forme (index >= 0, `Kind`
    // connu), il ne « gate » rien (commentaires corriges le 11/09). Le refus au boot repose donc
    // ENTIEREMENT sur la couture cablee ici, ce qui en fait exactement le genre de decision qu'un
    // test doit epingler.
    //
    // Ce n'est pas la meme restriction que « les leviers GPU se mesurent au banc, jamais en
    // appliquant » (close par la mesure du 09/09 : a `gpuWait` 0 %, un overclock ne rend
    // rien). Celle-ci porte sur le CONTEXTE : un offset GPU est VOLATILE, un reboot l'efface, et un
    // service LocalSystem qui le reecrirait sans surveillance pourrait degrader le demarrage.
    // Le vrai chemin produit — `DrDxt.ElevatedHelper.Program`, declenche par un geste de
    // l'utilisateur — cable les deux vraies plumes (`NvApiTuningActions`, `ADLXTuningActions`) et
    // ecrit reellement. Confondre les deux ferait croire a un oubli cote AMD.
    [Fact]
    public async Task Le_reconciliateur_de_boot_n_ecrit_le_GPU_chez_AUCUN_des_deux_fabricants()
    {
        using var sp = BuildProvider();
        var client = sp.GetRequiredService<IElevatedClient>();

        var amd = await client.ExecuteAsync(
            new ApplyAdlxTuningOp(GpuIndex: 0, AdlxTuningKind.Undervolt, IntValue: 2500), default);
        var nvidia = await client.ExecuteAsync(
            new ApplyNvApiPowerLimitOp(GpuIndex: 0, PowerLimitPercent: 90), default);

        // Le MOTIF est verifie, pas seulement l'echec : sur cette machine sans GPU AMD, la vraie
        // plume `ADLXTuningActions` echouerait elle aussi — mais avec « AMD ADLX unavailable ».
        // Exiger le motif du reconciliateur de boot est ce qui distingue « refus decide » de
        // « materiel absent », et c'est ce qui rend ce test rouge si quelqu'un cable la vraie
        // plume ici.
        amd.IsOk.Should().BeFalse();
        amd.Error!.Value.Message.Should().Contain("boot reconciler",
            "le refus doit venir de la DECISION, pas de l'absence de GPU AMD");
        nvidia.IsOk.Should().BeFalse();
        nvidia.Error!.Value.Message.Should().Contain("boot reconciler",
            "la meme restriction s'applique aux deux fabricants, pour la meme raison");
    }

    private static ServiceProvider BuildProviderWithStorage(IManifestStorage storage)
    {
        var services = new ServiceCollection();
        services.AddSingleton(storage);
        // 06/10/2026 : jamais la vraie clé de %ProgramData% (présente sur une machine où DrDXT a déjà signé).
        services.AddSingleton(HostServiceCollectionExtensions.Verificateur(
            Path.Combine(Path.GetTempPath(), "drdxt-sans-cle-" + Guid.NewGuid().ToString("N"), "manifest-public.key")));
        services.AddDrDxtReconcilerHost();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    // A provider whose IElevatedClient is the given (real) connect-only client, injected so
    // the host's TryAddSingleton does not win — lets the absent-helper test use a short
    // connect deadline instead of the production 5s wait.
    private static ServiceProvider BuildProviderWithElevatedClient(IElevatedClient client)
    {
        var services = new ServiceCollection();
        services.AddSingleton(client);
        services.AddDrDxtReconcilerHost();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    [Fact]
    public void Graph_Resolves_AllHostClasses()
    {
        using var sp = BuildProvider();

        sp.GetRequiredService<ReconcileTickService>().Should().NotBeNull();
        sp.GetRequiredService<SingleFlightReconcile>().Should().NotBeNull();
        sp.GetRequiredService<FeatureUpdateTrigger>().Should().NotBeNull();
        sp.GetRequiredService<BootSessionMarker>().Should().NotBeNull();
        sp.GetRequiredService<IReconciler>().Should().NotBeNull();
        sp.GetRequiredService<IDesiredStateStore>().Should().NotBeNull();
        sp.GetRequiredService<IElevatedReapplyClient>().Should().NotBeNull();
    }

    [Fact]
    public void DefaultElevatedClient_IsTheInProcessExecutor_NotABrokerChannel()
    {
        // Moat wiring Option B (M4-M5-moat-wiring-decision.md): the host's default
        // IElevatedClient is the IN-PROCESS executor — no open privileged IPC endpoint.
        using var sp = BuildProvider();

        sp.GetRequiredService<IElevatedClient>().Should().BeOfType<InProcessElevatedClient>(
            "the V1 reconciler reapplies in-process (LocalSystem), bounded by the signed manifest + allowlist");
    }

    [Fact]
    public void HostedService_Is_TheWorker()
    {
        using var sp = BuildProvider();

        // 28/09/2026 : une seconde tache de fond, la protection du ping portee par le service.
        var hosted = sp.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
        hosted.Should().BeEquivalentTo(new[] { typeof(Worker), typeof(ProtectionDuPingWorker) });
    }

    [Fact]
    public void Le_service_peut_poser_le_plafond_d_envoi_et_rien_d_autre_de_nouveau()
    {
        // Le texte de la construction, pas une execution : executer un ordre ici toucherait la vraie machine.
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var hote = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.ReconcilerHost", "HostServiceCollectionExtensions.cs"));
        var debut = hote.IndexOf("new PrivilegedOpExecutor(", StringComparison.Ordinal);
        var construction = hote[debut..hote.IndexOf("return new InProcessElevatedClient", debut, StringComparison.Ordinal)];

        construction.Should().Contain("qos: new Win32NetworkQosActions()");
        construction.Should().NotContain("firmwareBoot:").And.NotContain("manifeste:").And.NotContain("bcd:")
            .And.NotContain("plafondDuService:", "le service lit son reglage, il ne l'ecrit pas");
    }

    [Fact]
    public async Task ManifestStore_IsVerifyOnly_CannotSign()
    {
        using var sp = BuildProvider();
        var store = sp.GetRequiredService<IDesiredStateStore>();

        store.Should().BeOfType<SignedDesiredStateStore>();

        // A verify-only store refuses to commit (no signing key) — fail-clean, not a throw.
        var commit = await store.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>()));
        commit.IsOk.Should().BeFalse("the host can verify but NEVER sign");
        commit.Error!.Value.Code.Should().Be(ErrorCodes.OpNotAllowed);
    }

#if DEBUG
    // DEBUG/test build: no manifest-public.key file is provisioned in this test env, so the
    // host falls back to the EMBEDDED public key (DEBUG-only). A manifest produced by the
    // embedded dev signer VERIFIES and LOADS through the host's wired verify-only store. A
    // RELEASE build does not trust the embedded key — see the !DEBUG counterpart below.
    [Fact]
    public async Task Host_VerifiesAndLoads_AManifestSignedByTheEmbeddedKey()
    {
        var storage = new InMemoryManifestStorage();
        using var sp = BuildProviderWithStorage(storage);
        var policy = sp.GetRequiredService<IReconcilabilityPolicy>();

        // The App side: commit a real, signed manifest with the embedded dev signer into
        // the same storage the host reads.
        var committing = new SignedDesiredStateStore(
            storage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
            ManifestDevKeys.CreateVerifier().Value!, policy, new SystemClock());
        var desired = new DesiredState(new[]
        {
            new AppliedTweak("fixit.sanePowerPlan", "high", "balanced", DateTimeOffset.UtcNow),
        });
        (await committing.CommitAsync(desired)).IsOk.Should().BeTrue();

        // The host's wired verify-only store loads it (verifies under the embedded key).
        var hostStore = sp.GetRequiredService<IDesiredStateStore>();
        var loaded = await hostStore.LoadAsync();
        loaded.IsOk.Should().BeTrue("the host verifies real manifests with the embedded public key");
        loaded.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("fixit.sanePowerPlan");
    }
#else
    // RELEASE build (key custody / T21): the host never trusts the embedded dev key. With no
    // provisioned manifest-public.key, the host verifier is fail-closed, so even a manifest
    // correctly signed by the embedded DEV seed is REJECTED — the moat refuses to reapply
    // anything it cannot verify against a PROVISIONED production public key.
    [Fact]
    public async Task Host_InRelease_FailsClosed_EvenOnAnEmbeddedDevKeySignedManifest()
    {
        var storage = new InMemoryManifestStorage();
        using var sp = BuildProviderWithStorage(storage);
        var policy = sp.GetRequiredService<IReconcilabilityPolicy>();

        var committing = new SignedDesiredStateStore(
            storage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
            ManifestDevKeys.CreateVerifier().Value!, policy, new SystemClock());
        (await committing.CommitAsync(new DesiredState(new[]
        {
            new AppliedTweak("fixit.sanePowerPlan", "high", "balanced", DateTimeOffset.UtcNow),
        }))).IsOk.Should().BeTrue();

        var hostStore = sp.GetRequiredService<IDesiredStateStore>();
        var loaded = await hostStore.LoadAsync();
        loaded.IsOk.Should().BeFalse("a release host does not trust the embedded dev key (fail-closed)");
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }
#endif

    // A manifest signed by a DIFFERENT key still fails closed in the host: the embedded
    // verifier holds only OUR public key, so a foreign-signed manifest applies nothing.
    [Fact]
    public async Task Host_FailsClosed_OnAForeignSignedManifest()
    {
        var storage = new InMemoryManifestStorage();
        using var sp = BuildProviderWithStorage(storage);
        var policy = sp.GetRequiredService<IReconcilabilityPolicy>();

        var foreignSeed = new byte[32];
        for (var i = 0; i < foreignSeed.Length; i++) foreignSeed[i] = (byte)(0x40 + i);
        var foreignKey = NSec.Cryptography.Key.Import(
            NSec.Cryptography.SignatureAlgorithm.Ed25519, foreignSeed,
            NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        var foreignSigner = Ed25519ManifestSigner.FromSeed(foreignSeed);
        var foreignVerifier = new Ed25519ManifestVerifier(
            foreignKey.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));

        var foreignStore = new SignedDesiredStateStore(
            storage, foreignSigner, foreignVerifier, policy, new SystemClock());
        (await foreignStore.CommitAsync(new DesiredState(new[]
        {
            new AppliedTweak("fixit.sanePowerPlan", "high", "balanced", DateTimeOffset.UtcNow),
        }))).IsOk.Should().BeTrue();

        var hostStore = sp.GetRequiredService<IDesiredStateStore>();
        var loaded = await hostStore.LoadAsync();
        loaded.IsOk.Should().BeFalse("a foreign-signed manifest must fail closed (apply nothing)");
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }

    [Fact]
    public async Task ElevatedClient_FailsClean_WhenNoHelperServiceIsReachable_NeverFakesSuccess()
    {
        // The CONNECT-only handshake client is the RETAINED V2 broker path (the V1 default is
        // the in-process executor). Injected here so this test exercises the V2 client's
        // fail-clean behaviour directly: with no helper service listening, the reapply fails
        // CLEAN (HELPER_UNREACHABLE) — never a faked elevation success, never a crash.
        //
        // Test-hygiene: inject the same real connect-only client but with a DEAD pipe +
        // short connect deadline so the absent-helper fail-clean path returns in ~ms
        // instead of waiting the production 5s connect-retry budget. The client under test
        // is still the genuine HandshakingElevatedClient (it really tries to connect and
        // really fails clean) — only the timeout/pipe are tightened, never faked.
        var deadPipe = "DrDXT.HostComp." + Guid.NewGuid().ToString("N");
        using var sp = BuildProviderWithElevatedClient(
            HostElevatedClientFactory.CreateConnectOnly(pipeName: deadPipe, connectTimeoutMs: 50));
        var reapply = sp.GetRequiredService<IElevatedReapplyClient>();

        var result = await reapply.ReapplyAsync("fixit.sanePowerPlan", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", default);

        result.IsOk.Should().BeFalse("no helper service is reachable → fail-clean, never a faked success");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperUnreachable,
            "the real connect-only client surfaces HELPER_UNREACHABLE, never a faked success");
    }

    // Defence-in-depth: even a SINGLE flipped byte of the signed canonical content fails
    // closed in the host (the embedded verifier rejects it).
    [Fact]
    public async Task Host_FailsClosed_OnATamperedManifestByte()
    {
        var storage = new InMemoryManifestStorage();
        using var sp = BuildProviderWithStorage(storage);
        var policy = sp.GetRequiredService<IReconcilabilityPolicy>();

        var committing = new SignedDesiredStateStore(
            storage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
            ManifestDevKeys.CreateVerifier().Value!, policy, new SystemClock());
        (await committing.CommitAsync(new DesiredState(new[]
        {
            new AppliedTweak("fixit.sanePowerPlan", "high", "balanced", DateTimeOffset.UtcNow),
        }))).IsOk.Should().BeTrue();

        // Flip one byte of the signed canonical content (attacker edits the file at rest).
        var original = storage.Slot!;
        var chars = original.CanonicalJson.ToCharArray();
        chars[chars.Length / 2] = chars[chars.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = original with { CanonicalJson = new string(chars) };

        var hostStore = sp.GetRequiredService<IDesiredStateStore>();
        var loaded = await hostStore.LoadAsync();
        loaded.IsOk.Should().BeFalse("a tampered manifest must fail closed (apply nothing)");
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }

    // Local in-memory IManifestStorage seam (no real %ProgramData% / admin ACL). Mirrors
    // the Reconciler.Tests double; kept local since this project does not reference that
    // test assembly.
    private sealed class InMemoryManifestStorage : IManifestStorage
    {
        public SignedManifestEnvelope? Slot { get; set; }

        public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
        {
            Slot = envelope;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
            => Task.FromResult(Result<SignedManifestEnvelope?>.Ok(Slot));
    }
}
