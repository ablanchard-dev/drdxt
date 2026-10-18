using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Après une application validée, l'App committe un manifeste d'état voulu signé pour que
// le réconciliateur le réaffirme au démarrage (la douve de persistance).
//
//   * les leviers gardés (Applied && !AutoReverted) sont commités, exactement ceux-là, traduits
//     en identifiants du réconciliateur + jetons voulus ;
//   * un levier annulé automatiquement est exclu ;
//   * un échec de commit ne casse jamais l'expérience d'application : elle réussit toujours,
//     navigue vers le tableau de bord et affiche un signal non fatal « douve désarmée » (clé resx localisée) ;
//   * un levier non réconciliable ou non mappé est filtré (le mappeur et le magasin le
//     rejettent tous deux), donc le commit n'est même pas tenté quand rien ne se traduit.
//
// Chaque couture est un faux : aucune vraie clé de signature, aucun vrai registre, aucun vrai réconciliateur.
// -----------------------------------------------------------------------
public sealed class ScanApplyCommitTests
{
    private const int Score = 68;

    private static HardwareInventory Hw() => new(
        new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
        new[] { new GpuInfo("NVIDIA RTX 4070", "NVIDIA", 12L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static BenchmarkResult Bl => new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    private static ApplyOutcome Kept(string id, string originalJson = "{}")
        => new(id, Applied: true, AutoReverted: false, new TweakSnapshot(id, originalJson), Error: null);

    private static ApplyOutcome AutoReverted(string id)
        => new(id, Applied: true, AutoReverted: true, new TweakSnapshot(id, "{}"), Error: null);

    private static ApplyReport ReportOf(params ApplyOutcome[] outcomes)
        => new(outcomes.ToList(), new AbVerdict(Bl, Bl, false, "verdict.no_measurable_gain"));

    private sealed class Harness
    {
        public required ScanViewModel Vm { get; init; }
        public required IDesiredStateStore Store { get; init; }
        public required ITweakEngine Engine { get; init; }
    }

    private static Harness Build(
        Result<ApplyReport>? applyResult = null,
        Result? commitResult = null,
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(Bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St,
              new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) }, Bl)));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(Result.Ok());

        var engine = Substitute.For<ITweakEngine>();
        engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(applyResult ?? Result<ApplyReport>.Ok(ReportOf(Kept("fixit.power_plan"))));

        // Un consommateur de profil au plan non vide pour que l'application avance ; la logique de commit
        // testée dépend de l'ApplyReport du moteur (les leviers gardés), pas des identifiants du plan.
        var consumer = ScanViewModelTestSeams.ProfileConsumer(
            ScanViewModelTestSeams.PlanConsumption(new[] { "latency.hagsOn" }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(commitResult ?? Result.Ok());
        // Le rafraîchissement du droit à la douve en fin de scan est ici sans effet : rien n'est
        // encore armé au moment du scan, donc le seul commit vérifié ci-dessous est celui de l'application.
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        var vm = new ScanViewModel(
            hd, sr, br, sc, sd, safety, engine, consumer,
            store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key, journalLeviers: journalLeviers);

        return new Harness { Vm = vm, Store = store, Engine = engine };
    }

    private static async Task ScanThenApply(Harness h)
    {
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyCommand.ExecuteAsync(null);
    }

    // Le moteur pose dans la portée d'Optimiser : chaque écriture journalisée s'acquitte une fois, quand le journal des
    // leviers porte déjà son origine (c'est lui que « Annuler » relit).
    private static async Task<List<bool>> AcquitsDOptimiser(string fichier)
    {
        var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(fichier);
        var h = Build(journalLeviers: leviers);
        var acquits = new List<bool>();
        h.Engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                PorteeDePose.Courante?.Noter(() =>
                {
                    acquits.Add(leviers.Lire().Any(s => s.TweakId == "fixit.power_plan"));
                    return Task.FromResult(Result.Ok());
                });
                return Task.FromResult(Result<ApplyReport>.Ok(ReportOf(Kept("fixit.power_plan", DrDxt.Tests.Shared.InversesDeTest.Pour("fixit.power_plan")))));
            });
        await ScanThenApply(h);
        return acquits;
    }

    [Fact]
    public async Task Optimiser_acquitte_ses_poses_une_fois_apres_avoir_note_leur_origine()
    {
        var fichier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"leviers-{Guid.NewGuid():N}.json");
        try { (await AcquitsDOptimiser(fichier)).Should().Equal(true); }
        finally { System.IO.File.Delete(fichier); }
    }

    // Le journal des leviers ne s'écrit pas (son fichier est un dossier) : l'origine n'est notée nulle part chez l'App,
    // le helper la garde.
    [Fact]
    public async Task Optimiser_n_acquitte_rien_quand_le_journal_des_leviers_ne_s_ecrit_pas()
    {
        var dossier = System.IO.Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"leviers-{Guid.NewGuid():N}")).FullName;
        try { (await AcquitsDOptimiser(dossier)).Should().BeEmpty(); }
        finally { System.IO.Directory.Delete(dossier, recursive: true); }
    }

    // Optimiser peut garder plusieurs réglages : la phrase reste juste pour un comme pour plusieurs, sans accord à calculer.
    [Theory]
    [InlineData("fr", "l'inscrire", "l'annuler")]
    [InlineData("en", "list it", "undo it")]
    public void La_phrase_pas_inscrit_ne_depend_pas_du_nombre_de_reglages(string culture, string singulier, string singulier2)
    {
        var texte = string.Format(new System.Globalization.CultureInfo(culture),
            new DrDxt.Infrastructure.Localization.ResxLocalizer().Get("Leviers.PasInscrit", new System.Globalization.CultureInfo(culture)),
            "HAGS, Mode Jeu");
        texte.Should().Contain("HAGS, Mode Jeu").And.NotContain(singulier).And.NotContain(singulier2);
    }

    // Posé mais absent de « Ce que DrDXT a posé » : aucune ligne ne l'annule, l'écran le dit.
    [Fact]
    public async Task Optimiser_dit_qu_un_levier_pose_n_a_pas_pu_etre_inscrit()
    {
        var dossier = System.IO.Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"leviers-{Guid.NewGuid():N}")).FullName;
        try
        {
            var h = Build(journalLeviers: new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(dossier));
            await ScanThenApply(h);
            h.Vm.ResultatOptimisation.Should().Contain("Leviers.PasInscrit");
        }
        finally { System.IO.Directory.Delete(dossier, recursive: true); }
    }

    // Une application validée committe exactement les leviers gardés (traduits en identifiants du réconciliateur).
    [Fact]
    public async Task Validated_apply_commits_exactly_the_kept_tweaks()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(
            Kept("fixit.power_plan"),
            Kept("latency.reflex"))));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Is<DesiredState>(d =>
            d.Tweaks.Select(t => t.TweakId).OrderBy(x => x).SequenceEqual(
                new[] { "fixit.sanePowerPlan", "latency.reflex.lowLatencyMode" })));
        h.Vm.MoatUnarmedMessage.Should().BeNullOrEmpty("the commit succeeded → moat armed");
    }

    // Un levier annulé automatiquement n'est pas commité (seul le gardé l'est).
    [Fact]
    public async Task Auto_reverted_tweak_is_excluded_from_the_commit()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(
            Kept("fixit.power_plan"),
            AutoReverted("latency.reflex"))));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Is<DesiredState>(d =>
            d.Tweaks.Count == 1 && d.Tweaks[0].TweakId == "fixit.sanePowerPlan"));
    }

    // Un échec de commit ne casse jamais l'application : la navigation a lieu, aucun message bloquant,
    // and a non-fatal "moat unarmed" signal is surfaced (localized key).
    [Fact]
    public async Task Commit_failure_leaves_apply_successful_but_flags_moat_unarmed()
    {
        var h = Build(commitResult: Result.Fail(ErrorCodes.IoFailure, "disk full"));

        await ScanThenApply(h);

        h.Vm.ResultatOptimisation.Should().NotBeNullOrEmpty("apply succeeded and says what it set");
        h.Vm.ApplyBlockedMessage.Should().BeNullOrEmpty("apply itself succeeded — nothing is blocked");
        h.Vm.MoatUnarmedMessage.Should().Be("Apply.MoatUnarmed",
            "a commit failure surfaces a non-fatal localized 'moat not armed' signal");
        // La premisse, affirmee. Le `catch` generique de CommitDesiredStateAsync ecrit exactement
        // le meme message pour n'importe quelle panne de frontiere : sans cette ligne, le test
        // restait vert alors que le commit n'avait jamais eu lieu. Mesure du 11/09/2026 : un
        // `throw` place avant `ToDesiredTweaks` laissait ce test vert et rougissait ses deux voisins.
        await h.Store.Received(1).CommitAsync(Arg.Any<DesiredState>());
    }

    // Quand rien ne mène à une surface réconciliable, aucun manifeste n'est commité (la douve
    // n'a rien à armer), mais l'application réussit et navigue quand même.
    [Fact]
    public async Task No_reconcilable_kept_tweaks_commits_nothing_but_apply_succeeds()
    {
        // Le moteur n'a gardé qu'un levier GPU (non mappé / non réconciliable).
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(Kept("gpu.undervolt"))));

        await ScanThenApply(h);

        await h.Store.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());
        h.Vm.ResultatOptimisation.Should().NotBeNullOrEmpty("apply succeeded and says what it set");
        h.Vm.ApplyBlockedMessage.Should().BeNullOrEmpty();
        // Ce qui distingue le `return` DU `catch`. Les deux laissent le magasin intact ; seul le
        // `catch` ecrit « moat unarmed ». Rien a armer n'est pas une panne d'armement, et sans cette
        // ligne le test ne savait pas dire par quelle porte le commit n'avait pas eu lieu.
        h.Vm.MoatUnarmedMessage.Should().BeNullOrEmpty("nothing was reconcilable — that is not a failure to arm");
    }

    // Un levier de la session Windows (HKCU) gardé n'est pas réaffirmé par le service,
    // mais son origine entre au manifeste en RevertOnly, le seul que la désinstallation relit. Un seul envoi pour
    // toutes les origines (une invite UAC au plus) ; un refus est dit, levier par levier.
    private static string OrigineVrr => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
        @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", RegistryValueType.String,
        "SwapEffectUpgradeEnable=1;")).Value!;

    private static ApplyOutcome VrrGarde => new("latency.vrrFlipModelOn", Applied: true, AutoReverted: false,
        new TweakSnapshot("latency.vrrFlipModelOn", OrigineVrr, PrivilegedOpCodec.Serialize(new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings",
            RegistryValueType.String, "SwapEffectUpgradeEnable=1;VRROptimizeEnable=1")).Value), Error: null);

    [Fact]
    public async Task Un_levier_de_session_garde_inscrit_son_origine_pour_la_desinstallation()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(
            Kept("fixit.power_plan"),
            VrrGarde)));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Is<DesiredState>(d =>
            d.Tweaks.Count == 0 && d.RevertOnly!.Single().TweakId == "latency.vrrFlipModelOn"
            && d.RevertOnly!.Single().OriginalValueJson == OrigineVrr));
        h.Vm.MoatUnarmedMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Une_origine_que_le_manifeste_refuse_est_dite()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(VrrGarde)),
            commitResult: Result.Fail(ErrorCodes.IoFailure, "disk full"));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Any<DesiredState>());
        h.Vm.MoatUnarmedMessage.Should().Contain("Optimize.KeptOriginNotSigned").And.Contain("Erreur.Generique").And.NotContain("disk full");
    }

    // Une application ratée ne committe jamais (rien n'a été validé).
    [Fact]
    public async Task Failed_apply_never_commits()
    {
        var h = Build(applyResult: Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, "boom"));

        await ScanThenApply(h);

        await h.Store.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());
        h.Vm.ApplyBlockedMessage.Should().NotBeNullOrEmpty();
    }
}
