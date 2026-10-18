using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// L'app ne signe plus : elle envoie les entrees au HELPER eleve.
//
// Avant ce lot, rien n'atteignait le manifeste dans l'App installee : pas de graine d'environnement,
// pas d'elevation. Ces tests tiennent le nouveau chemin, de la composition jusqu'a l'ecran :
//   * l'injection de l'App ne compose aucun signataire ;
//   * CommitAsync = une CommitManifestOp au helper ; RafraichirAsync = un client qui ne lance pas l'UAC ;
//   * un refus « illisible » est garde, et la reprise explicite le renvoie avec le drapeau ;
//   * l'ecran nomme le refus et propose la reprise, puis DIT ce qu'elle a fait ;
//   * de bout en bout, sur un vrai tube : App → helper → cle de la machine → relecture verify-only.
// -----------------------------------------------------------------------
public sealed class ManifesteParLeHelperTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;

    private static string OrigineReflex(uint v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(
        RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode",
        RegistryValueType.DWord, v.ToString(CultureInfo.InvariantCulture))).Value!;

    private static AppliedTweak Correction() => new("latency.reflex.lowLatencyMode", "2", OrigineReflex(1), T);
    private static AppliedTweak Garde(string id = "latency.reflex") => new(id, OrigineReflex(2), OrigineReflex(1), T);

    // ── la composition ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void L_App_ne_compose_aucun_signataire_de_manifeste()
    {
        using var provider = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();

        provider.GetService<IManifestSigner>().Should().BeNull("la cle privee vit dans le helper eleve, jamais dans l'App");
        provider.GetService<IDesiredStateStore>().Should().NotBeNull();
    }

    // ── le magasin de l'App ──────────────────────────────────────────────────────────────────

    private static (HelperDesiredStateStore store, IElevatedClient helper, IElevatedClient sansLancer) Magasin()
    {
        var helper = Substitute.For<IElevatedClient>();
        var sansLancer = Substitute.For<IElevatedClient>();
        helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        sansLancer.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        return (new HelperDesiredStateStore(helper, sansLancer, Substitute.For<IDesiredStateStore>()), helper, sansLancer);
    }

    [Fact]
    public async Task Un_commit_envoie_les_entrees_au_helper_sans_drapeau_de_reprise()
    {
        var (store, helper, sansLancer) = Magasin();

        (await store.CommitAsync(new DesiredState(new[] { Correction() }, new[] { Garde() }))).IsOk.Should().BeTrue();

        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(op => EstCommit(op, false, 1, 1)), Arg.Any<CancellationToken>());
        await sansLancer.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task Le_rafraichissement_passe_par_le_client_qui_ne_lance_jamais_le_helper()
    {
        var (store, helper, sansLancer) = Magasin();

        await store.RafraichirAsync(new DesiredState(new[] { Correction() }));

        await sansLancer.Received(1).ExecuteAsync(Arg.Any<CommitManifestOp>(), Arg.Any<CancellationToken>());
        await helper.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task Les_refus_illisibles_sont_gardes_et_la_reprise_les_renvoie_tous_avec_le_drapeau()
    {
        var (store, helper, _) = Magasin();
        helper.ExecuteAsync(Arg.Is<PrivilegedOp>(op => SansReprise(op)), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(ErrorCodes.ManifestUnreadable, "illisible"));

        await store.CommitAsync(new DesiredState(new[] { Correction() }));
        await store.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), new[] { Garde() }));
        (await store.RepartirDUnManifesteVideAsync()).IsOk.Should().BeTrue();

        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(op => RepriseAvecLesDeuxRefus(op)), Arg.Any<CancellationToken>());
    }

    // Les predicats vivent hors des arbres d'expression de NSubstitute, qui refusent le filtrage par motif.
    private static bool EstCommit(PrivilegedOp op, bool repartir, int tweaks, int revertOnly)
        => op is CommitManifestOp c && c.RepartirDUnManifesteVide == repartir
           && c.Tweaks.Count == tweaks && c.RevertOnly.Count == revertOnly;

    private static bool SansReprise(PrivilegedOp op) => op is CommitManifestOp { RepartirDUnManifesteVide: false };

    private static bool RepriseAvecLesDeuxRefus(PrivilegedOp op)
        => op is CommitManifestOp { RepartirDUnManifesteVide: true } c
           && c.Tweaks.Single().TweakId == "latency.reflex.lowLatencyMode"
           && c.RevertOnly.Single().TweakId == "latency.reflex";

    // ── l'ecran ──────────────────────────────────────────────────────────────────────────────

    private static ScanViewModel Vm(IDesiredStateStore store)
    {
        var hw = new HardwareInventory(
            new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
            new[] { new GpuInfo("NVIDIA RTX 4070", "NVIDIA", 12L << 30, "551", TriState.On) },
            new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
            new MotherboardInfo("MSI", "B650"), new BiosInfo("1", DateTimeOffset.UtcNow, true), ChassisType.Desktop);
        var st = new SettingsState(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(hw));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(hw, st,
              new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) }, bl)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));
        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(Result.Ok());
        var moteur = Substitute.For<ITweakEngine>();
        moteur.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Result<ApplyReport>.Ok(new ApplyReport(
                  new List<ApplyOutcome> { new("fixit.power_plan", Applied: true, AutoReverted: false,
                      new TweakSnapshot("fixit.power_plan", "{}"), Error: null) },
                  new AbVerdict(bl, bl, false, "verdict.no_measurable_gain"))));

        return new ScanViewModel(
            hd, sr, br, sc, sd, safety, moteur,
            ScanViewModelTestSeams.ProfileConsumer(ScanViewModelTestSeams.PlanConsumption(new[] { "latency.hagsOn" })),
            store, new AppliedTweakManifestMapper(), ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);
    }

    private static IDesiredStateStore StoreQuiRefuse(string code)
    {
        var s = Substitute.For<IDesiredStateStore>();
        s.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Fail(code, "la raison du helper"));
        s.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));
        s.RepartirDUnManifesteVideAsync().Returns(Result.Ok());
        return s;
    }

    [Fact]
    public async Task L_ecran_nomme_le_manifeste_illisible_propose_la_reprise_puis_dit_ce_qu_elle_a_fait()
    {
        var store = StoreQuiRefuse(ErrorCodes.ManifestUnreadable);
        var vm = Vm(store);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        vm.ManifesteIllisibleMessage.Should().Be($"Apply.ManifestUnreadable{Docteur.DeuxPoints}Erreur.Generique");
        vm.PeutRepartirDuManifeste.Should().BeTrue();
        vm.MoatUnarmedMessage.Should().Be("Apply.MoatUnarmed");

        await vm.RepartirDuManifesteCommand.ExecuteAsync(null);

        await store.Received(1).RepartirDUnManifesteVideAsync();
        vm.ManifesteIllisibleMessage.Should().Be("Apply.ManifestResetDone");
        vm.PeutRepartirDuManifeste.Should().BeFalse();
    }

    [Fact]
    public void L_App_n_ecrit_plus_la_reference_de_derive_et_son_rafraichissement_ne_passe_par_aucun_decorateur()
    {
        // Le decorateur ApplyReferenceSeedingStore vivait ici : l'App non elevee ecrivait la
        // racine admin (echec avale), et son RafraichirAsync par defaut repassait par le client qui lance l'UAC.
        using var provider = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();

        provider.GetRequiredService<IDesiredStateStore>().Should().BeOfType<HelperDesiredStateStore>(
            "la reference de derive est posee par le helper eleve, dans le CommitManifestOp");
    }

    [Fact]
    public async Task L_ecran_dit_que_le_repere_de_derive_manque_sans_pretendre_que_rien_n_est_arme()
    {
        var vm = Vm(StoreQuiRefuse(ErrorCodes.ManifestReferenceNotSeeded));

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        vm.MoatUnarmedMessage.Should().Be($"Apply.ReferenceNotSeeded{Docteur.DeuxPoints}Erreur.Generique");
        vm.ManifesteIllisibleMessage.Should().BeNull();
    }

    [Fact]
    public async Task Une_panne_passagere_n_ouvre_pas_la_reprise()
    {
        var vm = Vm(StoreQuiRefuse(ErrorCodes.HelperUnreachable));

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        vm.MoatUnarmedMessage.Should().Be("Apply.MoatUnarmed");
        vm.ManifesteIllisibleMessage.Should().BeNull("jeter un manifeste pour un helper injoignable serait absurde");
        vm.PeutRepartirDuManifeste.Should().BeFalse();
    }

    // ── de bout en bout ──────────────────────────────────────────────────────────────────────

    private sealed class Autoriser : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    /// <summary>La relecture de l'App : verify-only, avec la cle publique posee par le helper, relue a
    /// chaque fois (elle n'existe qu'apres le premier commit).</summary>
    private sealed class LectureVerifyOnly(string racine) : IDesiredStateStore
    {
        public Task<Result> CommitAsync(DesiredState state) => throw new InvalidOperationException("verify-only");

        public async Task<Result<DesiredState>> LoadAsync()
        {
            var v = AppManifestPublicKey.LoadVerifier(Path.Combine(racine, MachineManifestKey.PublicFileName), enforceTrust: false);
            if (!v.IsOk) return Result<DesiredState>.Fail(v.Error!.Value.Code, v.Error.Value.Message);
            return await new SignedDesiredStateStore(new ProgramDataManifestStorage(racine, applyAcl: false),
                signer: null, v.Value!, new DefaultReconcilabilityPolicy(), new SystemClock()).LoadAsync();
        }
    }

    [Fact]
    public async Task De_bout_en_bout_l_App_commite_le_helper_signe_et_l_App_relit_sans_jamais_tenir_la_cle()
    {
        var racine = Path.Combine(Path.GetTempPath(), "drdxt-bout-en-bout-" + Guid.NewGuid().ToString("N"));
        var tube = "DrDXT.CommitE2E." + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var executeur = new PrivilegedOpExecutor(
                Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
                Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
                Substitute.For<IADLXTuningActions>(), firmwareBoot: null,
                manifeste: new ManifestCommitActions(racine, appliquerAcl: false));
            var serveur = new HandshakeAcceptingServer(new Autoriser(), new CryptoSessionKeyGenerator(), new OpAllowlist(), executeur, tube);
            var boucle = Task.Run(async () =>
            {
                try { while (!cts.IsCancellationRequested) await serveur.AcceptOneAsync(cts.Token); }
                catch (OperationCanceledException) { /* fin du test */ }
            });

            // Le vrai client de production, garde du serveur active (l'image attendue est ce processus).
            var client = new HandshakingElevatedClient(tube, Environment.ProcessPath!);
            var store = new HelperDesiredStateStore(client, client, new LectureVerifyOnly(racine));

            (await store.CommitAsync(new DesiredState(new[] { Correction() }))).IsOk.Should().BeTrue();
            (await store.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), new[] { Garde() }))).IsOk.Should().BeTrue();

            var relu = await store.LoadAsync();
            relu.IsOk.Should().BeTrue();
            relu.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("latency.reflex.lowLatencyMode");
            relu.Value.RevertOnly.Should().ContainSingle().Which.TweakId.Should().Be("latency.reflex");

            // Le manifeste est altere sur le disque : le commit suivant est refuse, nomme, a travers le tube.
            await File.WriteAllTextAsync(Path.Combine(racine, "desired-state.manifest"), "{altere");
            var refuse = await store.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), new[] { Garde("fixit.power_plan") }));
            refuse.Error!.Value.Code.Should().Be(ErrorCodes.ManifestUnreadable);

            // La reprise explicite : l'altere est mis de cote, le refuse entre dans un manifeste neuf.
            (await store.RepartirDUnManifesteVideAsync()).IsOk.Should().BeTrue();
            var apres = await store.LoadAsync();
            apres.Value!.RevertOnly.Should().ContainSingle().Which.TweakId.Should().Be("fixit.power_plan");
            Directory.GetFiles(racine, "desired-state.manifest.mis-de-cote-*").Should().ContainSingle();

            cts.Cancel();
            await boucle;
        }
        finally
        {
            cts.Cancel();
            try { Directory.Delete(racine, recursive: true); } catch { /* dossier temporaire */ }
        }
    }
}
