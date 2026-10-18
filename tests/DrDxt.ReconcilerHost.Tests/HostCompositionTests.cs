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
// Fumée de composition : le graphe DI de production se résout de bout en bout, et le magasin
// de manifeste est en vérification seule (il ne peut jamais signer sur l'hôte).
// -----------------------------------------------------------------------
public sealed class HostCompositionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        // Le vrai journal réparerait %ProgramData%\DrDxt : jamais depuis un test.
        services.AddSingleton(new JournalDuService(null));
        services.AddDrDxtReconcilerHost();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    // Un fournisseur dont le stockage de manifeste est une couture en mémoire injectée (préenregistrée
    // pour que le TryAddSingleton de l'hôte ne l'écrase pas). Les tests de vérification du manifeste
    // font ainsi l'aller-retour sans toucher au vrai %ProgramData% ni à l'ACL admin.
    [Fact]
    public async Task The_boot_reconciler_can_NEVER_reboot_the_machine_into_the_BIOS()
    {
        // Cet hote tourne sans SURVEILLANCE. Un redemarrage qu'il aurait declenche
        // arriverait chez le client comme une machine qui repart dans le BIOS toute seule,
        // sans rien a l'ecran pour l'expliquer -- et un reboot sans cause visible detruit la
        // confiance plus surement que n'importe quel bug. La couture firmware n'est donc
        // deliberement pas passee ici, exactement comme les coutures GPU juste au-dessus.
        //
        // Ce test epingle la decision de composition, pas seulement le comportement de
        // l'executeur : il resout le vrai graphe de production et lui envoie l'op.
        using var sp = BuildProvider();
        var client = sp.GetRequiredService<IElevatedClient>();

        var r = await client.ExecuteAsync(new RebootToFirmwareSetupOp(10), default);

        r.IsOk.Should().BeFalse();
        // Et l'echec est DIT : cabler la couture ici ferait passer ce test au vert, donc
        // quiconque la cable devra lire ce commentaire et assumer le changement.
        r.Error!.Value.Message.Should().Contain("not wired");
    }

    // Le jumeau GPU du test ci-dessus.
    //
    // L'hote de boot refuse d'ecrire le GPU : la composition cable `DisabledNvApiActions` /
    // `DisabledAdlxActions`. Il n'y a pas de seconde barriere : `OpAllowlist.ValidateAdlx` accepte
    // un `ApplyAdlxTuningOp` bien forme (index >= 0, `Kind` connu). Le refus au boot repose donc
    // ENTIEREMENT sur la couture cablee ici, exactement le genre de decision qu'un test doit epingler.
    //
    // Ce n'est pas la meme restriction que « les leviers GPU se mesurent au banc, jamais en
    // appliquant » (mesure du 09/09 : a `gpuWait` 0 %, un overclock ne rend rien). Celle-ci porte sur le CONTEXTE : un offset GPU est VOLATILE, un reboot l'efface, et un
    // service LocalSystem qui le reecrirait sans surveillance pourrait degrader le demarrage.
    // Le vrai chemin produit (`DrDxt.ElevatedHelper.Program`, declenche par un geste de
    // l'utilisateur) cable les deux vraies plumes (`NvApiTuningActions`, `ADLXTuningActions`) et
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
        // Jamais la vraie clé de %ProgramData% (présente sur une machine où DrDXT a déjà signé).
        services.AddSingleton(HostServiceCollectionExtensions.Verificateur(
            Path.Combine(Path.GetTempPath(), "drdxt-sans-cle-" + Guid.NewGuid().ToString("N"), "manifest-public.key")));
        services.AddDrDxtReconcilerHost();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    // Un fournisseur dont l'IElevatedClient est le (vrai) client en connexion seule donné, injecté
    // pour que le TryAddSingleton de l'hôte ne gagne pas : le test du helper absent peut utiliser une
    // échéance de connexion courte au lieu des 5 s de production.
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
        // Câblage de la douve retenu : l'IElevatedClient par
        // défaut de l'hôte est l'exécuteur en processus, sans point d'entrée IPC privilégié ouvert.
        using var sp = BuildProvider();

        sp.GetRequiredService<IElevatedClient>().Should().BeOfType<InProcessElevatedClient>(
            "the V1 reconciler reapplies in-process (LocalSystem), bounded by the signed manifest + allowlist");
    }

    [Fact]
    public void HostedService_Is_TheWorker()
    {
        using var sp = BuildProvider();

        // Une seconde tache de fond, la protection du ping portee par le service.
        var hosted = sp.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
        // Une troisième, le tube de l'état du processeur, en lecture seule.
        hosted.Should().BeEquivalentTo(new[] { typeof(RejeuAuDemarrage), typeof(Worker), typeof(ProtectionDuPingWorker),
            typeof(ServeurDuProcesseur), typeof(TemoinDIncidentWorker) });
        // Le rejeu démarre avant la première réconciliation.
        hosted.IndexOf(typeof(RejeuAuDemarrage)).Should().BeLessThan(hosted.IndexOf(typeof(Worker)));
        // Et le Worker attend sa fin avant la première passe, même quand il attend la fin d'une partie.
        var worker = sp.GetServices<IHostedService>().OfType<Worker>().Single();
        typeof(Worker).GetField("_finDuRejeu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(worker).Should().NotBeNull();
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

        // Un magasin en vérification seule refuse de committer (pas de clé de signature) : échec propre, pas une exception.
        var commit = await store.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>()));
        commit.IsOk.Should().BeFalse("the host can verify but NEVER sign");
        commit.Error!.Value.Code.Should().Be(ErrorCodes.OpNotAllowed);
    }

#if DEBUG
    // Version Debug/test : aucun fichier manifest-public.key n'est provisionné dans cet environnement,
    // donc l'hôte se replie sur la clé publique embarquée (Debug seulement). Un manifeste produit par
    // le signataire de dev embarqué se vérifie et se charge par le magasin câblé de l'hôte. Une
    // version Release ne fait pas confiance à la clé embarquée : voir le pendant !DEBUG ci-dessous.
    [Fact]
    public async Task Host_VerifiesAndLoads_AManifestSignedByTheEmbeddedKey()
    {
        var storage = new InMemoryManifestStorage();
        using var sp = BuildProviderWithStorage(storage);
        var policy = sp.GetRequiredService<IReconcilabilityPolicy>();

        // Côté App : committer un vrai manifeste signé avec le signataire de dev embarqué dans
        // le même stockage que l'hôte lit.
        var committing = new SignedDesiredStateStore(
            storage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
            ManifestDevKeys.CreateVerifier().Value!, policy, new SystemClock());
        var desired = new DesiredState(new[]
        {
            new AppliedTweak("fixit.sanePowerPlan", "high", "balanced", DateTimeOffset.UtcNow),
        });
        (await committing.CommitAsync(desired)).IsOk.Should().BeTrue();

        // Le magasin câblé de l'hôte, vérification seule, le charge (vérifie sous la clé embarquée).
        var hostStore = sp.GetRequiredService<IDesiredStateStore>();
        var loaded = await hostStore.LoadAsync();
        loaded.IsOk.Should().BeTrue("the host verifies real manifests with the embedded public key");
        loaded.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("fixit.sanePowerPlan");
    }
#else
    // Version Release (garde des clés) : l'hôte ne fait jamais confiance à la clé de dev embarquée.
    // Sans manifest-public.key provisionné, le vérificateur de l'hôte est fermé, donc même un manifeste
    // correctement signé par la graine de dev embarquée est rejeté : la douve refuse de réappliquer
    // quoi que ce soit qu'elle ne peut vérifier contre une clé publique de production provisionnée.
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

    // Un manifeste signé par une autre clé se ferme toujours dans l'hôte : le vérificateur
    // embarqué ne tient que notre clé publique, donc un manifeste signé ailleurs n'applique rien.
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
        // Le client de négociation en connexion seule est le chemin de courtier V2 conservé (le défaut
        // V1 est l'exécuteur en processus). Injecté ici pour que ce test exerce directement l'échec
        // propre du client V2 : sans service helper à l'écoute, la réapplication échoue proprement
        // (HELPER_UNREACHABLE), jamais une élévation réussie inventée, jamais un plantage.
        //
        // Hygiène de test : injecter le même vrai client en connexion seule mais avec un tube mort et
        // une échéance de connexion courte, pour que le chemin d'échec propre du helper absent rende en
        // quelques ms au lieu d'attendre les 5 s de reconnexion de production. Le client testé reste
        // le vrai HandshakingElevatedClient (il tente vraiment de se connecter et échoue vraiment
        // proprement) ; seuls le délai et le tube sont resserrés, jamais simulés.
        var deadPipe = "DrDXT.HostComp." + Guid.NewGuid().ToString("N");
        using var sp = BuildProviderWithElevatedClient(
            HostElevatedClientFactory.CreateConnectOnly(pipeName: deadPipe, connectTimeoutMs: 50));
        var reapply = sp.GetRequiredService<IElevatedReapplyClient>();

        var result = await reapply.ReapplyAsync("fixit.sanePowerPlan", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", default);

        result.IsOk.Should().BeFalse("no helper service is reachable → fail-clean, never a faked success");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperUnreachable,
            "the real connect-only client surfaces HELPER_UNREACHABLE, never a faked success");
    }

    // Défense en profondeur : même un seul octet inversé du contenu canonique signé se ferme
    // dans l'hôte (le vérificateur embarqué le rejette).
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

        // Inverser un octet du contenu canonique signé (l'attaquant édite le fichier au repos).
        var original = storage.Slot!;
        var chars = original.CanonicalJson.ToCharArray();
        chars[chars.Length / 2] = chars[chars.Length / 2] == 'x' ? 'y' : 'x';
        storage.Slot = original with { CanonicalJson = new string(chars) };

        var hostStore = sp.GetRequiredService<IDesiredStateStore>();
        var loaded = await hostStore.LoadAsync();
        loaded.IsOk.Should().BeFalse("a tampered manifest must fail closed (apply nothing)");
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }

    // Couture IManifestStorage locale en mémoire (ni vrai %ProgramData% ni ACL admin). Miroir de
    // la doublure de Reconciler.Tests ; gardée locale parce que ce projet ne référence pas cet
    // assembly de test.
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
