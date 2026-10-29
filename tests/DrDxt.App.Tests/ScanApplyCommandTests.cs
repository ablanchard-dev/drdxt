using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// « OPTIMISER ».
//
//   * juger avant d'encaisser : rien a tenter ⇒ motifs, et jamais l'achat ;
//   * gate paye, puis point de restauration, avant toute ecriture ;
//   * ce qui ne se mesure pas (effet au redemarrage) part au moteur sans baseline ;
//   * Un seul levier a chaud part en session mesuree ; un levier ecarte n'est jamais applique.
//
// Chaque couture est un faux : aucune vraie élévation, aucun vrai registre, aucune vraie restauration système.
// -----------------------------------------------------------------------
public sealed partial class ScanApplyCommandTests
{
    private const int Score = 68;
    private const int PidDuJeu = 4242;

    private static readonly string[] AuRedemarrage = { "latency.hagsOn", "latency.gameModeOn" };
    private static readonly string[] AChaud = { "fixit.power_plan", "latency.reflex" };

    // ---- faux partagés ------------------------------------------------

    private static HardwareInventory Hw(ChassisType c = ChassisType.Desktop) => new(
        new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
        new[] { new GpuInfo("NVIDIA RTX 4070", "NVIDIA", 12L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        c);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static BenchmarkResult Bl => new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    private static ApplyReport OkReport(params string[] appliedIds) =>
        new(appliedIds.Select(id => new ApplyOutcome(id, Applied: true, AutoReverted: false,
                new TweakSnapshot(id, "{}"), Error: null)).ToList(),
            Verdict: null);

    /// <summary>Un levier a chaud de laboratoire : il s'applique, se relit, se rend.</summary>
    private sealed class CandidatFaux(CandidateDescriptor d) : IAutopilotCandidate
    {
        public CandidateDescriptor Descriptor => d;
        public Result Apply() => Result.Ok();
        public Result Revert() => Result.Ok();
        public Result<bool> ReadBack() => Result<bool>.Ok(true);
    }

    private sealed class Harness
    {
        public required ScanViewModel Vm { get; init; }
        public DrDxt.App.Platform.IGameLocator? Locator { get; init; }
        public required ISafetyManager Safety { get; init; }
        public required ITweakEngine Engine { get; init; }
        public required List<string> CandidatsCrees { get; init; }
    }

    private static Harness Build(
        string[]? graines = null,
        ScanProfileConsumption? consumption = null,
        Result? restoreResult = null,
        Result<ApplyReport>? applyResult = null,
        int pid = 0,
        int arretsRecents = 0,
        DrDxt.Application.Autopilot.JournalDesCandidats? journal = null,
        string? nomDuJeu = null,
        IBenchmarkRunner? banc = null,
        Func<CandidateDescriptor, IAutopilotCandidate>? creer = null,
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = banc ?? Substitute.For<IBenchmarkRunner>();
        if (banc is null)
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
        safety.EnsureRestorePointAsync().Returns(restoreResult ?? Result.Ok());

        var engine = Substitute.For<ITweakEngine>();
        engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(applyResult ?? Result<ApplyReport>.Ok(OkReport(AuRedemarrage)));

        var consumer = ScanViewModelTestSeams.ProfileConsumer(
            consumption ?? ScanViewModelTestSeams.PlanConsumption(graines ?? AuRedemarrage));

        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.LocateRunningGamePid().Returns(pid > 0 ? pid : null);
        if (nomDuJeu is not null) locator.NameOfGame(pid).Returns(nomDuJeu);

        var crees = new List<string>();
        IReadOnlyList<ArretClasse> arrets = Enumerable.Range(1, arretsRecents)
            .Select(j => ArretClasse.SansDetail(DateTimeOffset.UtcNow.AddDays(-j))).ToList();

        var vm = new ScanViewModel(
            hd, sr, br, sc, sd, safety, engine, consumer,
            ScanViewModelTestSeams.DesiredStateStore(),
            new AppliedTweakManifestMapper(), ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key,
            gameLocator: locator,
            creerCandidat: d => { crees.Add(d.Id); return creer?.Invoke(d) ?? new CandidatFaux(d); },
            lireStabilite: () => (null, arrets),
            journalCandidats: journal,
            journalLeviers: journalLeviers);

        return new Harness { Vm = vm, Safety = safety, Engine = engine, CandidatsCrees = crees, Locator = locator };
    }

    private static async Task ScanThenApply(Harness h)
    {
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyCommand.ExecuteAsync(null);
    }

    private static Task EngineReceived(Harness h, int fois) =>
        h.Engine.Received(fois).ApplyAsync(
            Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

    // ====================== enablement ======================

    [Fact]
    public void Apply_is_disabled_before_a_scan_has_results()
        => Build().Vm.ApplyCommand.CanExecute(null).Should().BeFalse();

    [Fact]
    public async Task Apply_becomes_enabled_after_a_successful_scan()
    {
        var h = Build();
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Vm.ApplyCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Optimiser_est_offert_a_tous_c_est_lui_qui_juge()
    {
        // DrDXT est gratuit : aucune licence ne conditionne l'action principale.
        var h = Build();
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.CanApply.Should().BeTrue();
    }

    // ====================== le cerveau : juger, puis un levier ======================

    [Fact]
    public async Task Les_corrections_au_redemarrage_vont_au_moteur_SANS_baseline_et_sans_levier_a_chaud()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "latency.gameModeOn", "fixit.power_plan" });
        await ScanThenApply(h);

        await h.Engine.Received(1).ApplyAsync(
            Arg.Is<TweakPlan>(p => p.TweakIds.OrderBy(x => x).SequenceEqual(new[] { "latency.gameModeOn", "latency.hagsOn" })),
            Arg.Is<BenchmarkResult?>(b => b == null),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.RebootSet");
    }

    // Le résultat dit le moment d'une pose, pas son effet ; une pose que la relecture ne confirme pas porte « non relu ».
    [Theory]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Applied, false)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Unknown, true)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Unavailable, true)]
    public async Task Une_pose_non_relue_se_dit_non_relue(DrDxt.Application.Tweaks.EtatApplique relu, bool ditNonRelu)
    {
        var h = Build(graines: new[] { "latency.hagsOn", "latency.gameModeOn" });
        h.Vm.VerifierLaPose = _ => relu;
        await ScanThenApply(h);

        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.RebootSet");
        if (ditNonRelu) h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.NonRelu");
        else h.Vm.ResultatOptimisation.Should().NotContain("Apply.Result.NonRelu");
    }

    // Le moteur défait un lot entier sur un échec. Un mode d'écran que le joueur ne garde pas ne
    // doit pas défaire les heures d'activité ni les corrections au redémarrage cliquées avec lui : chacun son lot.
    [Fact]
    public async Task Un_mode_d_ecran_refuse_ne_defait_pas_les_autres_reglages_au_clic()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "fixit.heures_actives", "fixit.ecran_frequence" });
        h.Engine.ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.Contains("fixit.ecran_frequence")), Arg.Any<BenchmarkResult?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ApplyReport>.Fail(ErrorCodes.TweakSdkUnavailable, "le joueur ne garde pas le mode"));
        await ScanThenApply(h);

        await h.Engine.Received(1).ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.SequenceEqual(new[] { "fixit.heures_actives" })),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await h.Engine.Received(1).ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.SequenceEqual(new[] { "latency.hagsOn" })),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ApplyBlockedMessage.Should().BeNullOrEmpty();
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.NotHeld", "F-16 : le lot refusé se nomme, il ne disparaît pas");
    }

    // Un lot que le moteur n'a pas pu défaire en entier : un levier peut être resté posé. « Rien n'a été conservé » ou
    // « rien n'est resté » serait faux ; l'écran dit de vérifier ces réglages.
    [Fact]
    public async Task Un_lot_pas_defait_en_entier_ne_se_dit_jamais_inchange()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "latency.gameModeOn" },
            applyResult: Result<ApplyReport>.Fail(ErrorCodes.TweakPartiallyApplied, "rollback could not revert: latency.hagsOn"));
        await ScanThenApply(h);

        h.Vm.ApplyBlockedMessage.Should().StartWith("Apply.Result.ResteAVerifier").And.NotBe("Apply.Failed");
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.ResteAVerifier")
            .And.NotContain("Apply.Result.NotHeld").And.NotContain("Apply.Result.RebootNoneHeld");
    }

    [Fact]
    public async Task Parmi_d_autres_lots_celui_pas_defait_en_entier_est_dit_a_verifier()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "fixit.heures_actives", "fixit.ecran_frequence" });
        h.Engine.ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.Contains("fixit.ecran_frequence")), Arg.Any<BenchmarkResult?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ApplyReport>.Fail(ErrorCodes.TweakPartiallyApplied, "rollback could not revert: fixit.ecran_frequence"));
        await ScanThenApply(h);

        var lignes = h.Vm.ResultatOptimisation!.Split(Environment.NewLine);
        lignes.Should().Contain(l => l.StartsWith("Apply.Result.ResteAVerifier") && l.Contains("fixit.ecran_frequence"));
        lignes.Where(l => l.StartsWith("Apply.Result.NotHeld")).Should().NotContain(l => l.Contains("fixit.ecran_frequence"),
            "un réglage peut-être resté posé n'est pas « rien n'est resté »");
    }

    [Fact]
    public async Task Le_reglage_au_clic_pas_defait_en_entier_ne_se_dit_pas_sans_effet()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "fixit.ecran_frequence" });
        h.Engine.ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.Contains("fixit.ecran_frequence")), Arg.Any<BenchmarkResult?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ApplyReport>.Fail(ErrorCodes.TweakPartiallyApplied, "rollback could not revert: fixit.ecran_frequence"));
        await ScanThenApply(h);

        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.ResteAVerifier").And.NotContain("Apply.Result.SetNowNoneHeld");
    }

    [Fact]
    public async Task Le_lot_au_redemarrage_pas_defait_en_entier_ne_se_dit_pas_sans_effet()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "fixit.heures_actives" });
        h.Engine.ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.Contains("latency.hagsOn")), Arg.Any<BenchmarkResult?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ApplyReport>.Fail(ErrorCodes.TweakPartiallyApplied, "rollback could not revert: latency.hagsOn"));
        h.Engine.ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.Contains("fixit.heures_actives")), Arg.Any<BenchmarkResult?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ApplyReport>.Ok(OkReport("fixit.heures_actives")));
        await ScanThenApply(h);

        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.ResteAVerifier").And.NotContain("Apply.Result.RebootNoneHeld");
    }

    [Fact]
    public async Task Quand_Windows_garde_un_point_existant_l_ecran_dit_lequel_protege()
    {
        // Un 2e « Optimiser » dans la journee n'est plus bloque ; le point que Windows a
        // garde protege le changement, et le client doit savoir de quand il date.
        var h = Build(graines: new[] { "latency.hagsOn", "latency.gameModeOn", "fixit.power_plan" });
        h.Safety.PointReutiliseUtc.Returns(new DateTime(2026, 9, 14, 7, 0, 0, DateTimeKind.Utc));

        await ScanThenApply(h);

        h.Vm.ResultatOptimisation.Should().Contain("Apply.RestorePoint.Reused");
    }

    [Fact]
    public async Task Un_SEUL_levier_a_chaud_part_en_session()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await ScanThenApply(h);

        h.CandidatsCrees.Should().Equal(new[] { "fixit.power_plan" }, "un seul KeepPolicy : jamais deux leviers");
        await EngineReceived(h, 0);
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.ToMeasure · tweak.fixit.power_plan.title")
            .And.Contain("Apply.Result.NotThisRound · tweak.latency.reflex.title")
            .And.Contain("personne n'a dit ce qui était joué", "aucune scene n'est fournie dans ce test : le contrat doit le dire");
    }

    // Sur un seul écran, le clic sur « Optimiser » réduit un plein écran exclusif. La session
    // attend que le jeu revienne à l'écran ; s'il reste réduit, elle n'a pas lieu et c'est dit.
    [Fact]
    public async Task Jeu_reduit_au_clic_la_session_attend_son_retour_puis_a_lieu()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Locator!.LocateRunningGamePid().Returns((int?)null, (int?)null, PidDuJeu);
        h.Locator.JeuOuvert().Returns(true);
        var attentes = new List<bool>();
        h.Vm.Patienter = _ => { attentes.Add(h.Vm.AttenteDuJeuSession && !h.Vm.AttenteDuJeu); return Task.CompletedTask; };

        await h.Vm.ApplyCommand.ExecuteAsync(null);

        // Deux pas d'attente (le jeu revient au second), puis ceux de la surveillance de la session.
        attentes.Should().HaveCountGreaterThanOrEqualTo(2);
        attentes.Take(2).Should().OnlyContain(a => a);
        h.CandidatsCrees.Should().Equal(new[] { "fixit.power_plan" });
        h.Vm.ResultatOptimisation.Should().NotContain("Apply.Session.JeuReduit");
    }

    // Le joueur revient sur DrDXT en pleine session : le jeu est réduit, l'A/B mêle partie et jeu réduit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quitter_le_jeu_pendant_la_session_est_dit_avec_le_verdict(bool quitte)
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        if (quitte) h.Locator!.LocateRunningGamePid().Returns(PidDuJeu, PidDuJeu, (int?)null);
        h.Vm.Patienter = _ => Task.CompletedTask;

        await h.Vm.ApplyCommand.ExecuteAsync(null);

        h.CandidatsCrees.Should().Equal(new[] { "fixit.power_plan" }, "temoin : la session a bien eu lieu");
        if (quitte) h.Vm.ResultatOptimisation.Should().Contain("Apply.Session.JeuQuitte");
        else h.Vm.ResultatOptimisation.Should().NotContain("Apply.Session.JeuQuitte");
    }

    // Un levier que la session garde, et que le journal des leviers n'inscrit pas : posé sans ligne « Annuler », c'est dit.
    private sealed class Machine { public bool Pose; }

    private static string OpReflex(int v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
        @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", RegistryValueType.DWord,
        v.ToString(System.Globalization.CultureInfo.InvariantCulture))).Value!;

    private sealed class LevierReflex(Machine m, string id) : ITweak
    {
        public string Id => id;
        public TweakCategory Category => TweakCategory.Latency;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "tweak." + id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            m.Pose = true;
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, OpReflex(1), OpReflex(2))));
        }
        public Task<Result> RevertAsync(TweakSnapshot snapshot) { m.Pose = false; return Task.FromResult(Result.Ok()); }
    }

    private sealed class RegistreReflex(Machine m) : DrDxt.Application.Tweaks.Hardware.IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) => Result<uint?>.Ok(m.Pose ? 2u : 1u);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
    }

    // Posé, le levier améliore le p95 : la session le garde.
    private sealed class BancReflex(Machine m) : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
            => Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(100, 60, 48, new List<double> { 10.0 }, null, 1.0, runs,
                P95FrametimeMs: m.Pose ? 11.0 : 12.0, HygieneKeptFraction: 1.0)));
    }

    [Fact]
    public async Task Un_levier_garde_en_session_et_non_inscrit_le_dit()
    {
        var dossier = System.IO.Directory.CreateTempSubdirectory("drdxt-session-").FullName;
        try
        {
            var m = new Machine();
            var h = Build(graines: AChaud, pid: PidDuJeu, banc: new BancReflex(m),
                journalLeviers: new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(dossier),
                creer: d => new DrDxt.Application.Autopilot.TweakCandidate(new LevierReflex(m, d.Id), d, new RegistreReflex(m),
                    new DrDxt.Application.Autopilot.FichierRevertEnAttente(System.IO.Path.Combine(dossier, "revert.json"))));
            await h.Vm.ScanCommand.ExecuteAsync(null);
            h.Vm.Patienter = _ => Task.CompletedTask;

            await h.Vm.ApplyCommand.ExecuteAsync(null);

            m.Pose.Should().BeTrue("témoin : la session a gardé le levier");
            h.Vm.ResultatOptimisation.Should().Contain("Leviers.PasInscrit");
        }
        finally { System.IO.Directory.Delete(dossier, recursive: true); }
    }

    // Un levier gardé par la session restait dans les graines du clic suivant : un nouveau candidat relisait la cible déjà
    // posée comme « origine », et l'A/B comparait la cible à elle-même. Gardé, il sort des graines, comme les poses
    // d'Optimiser (et comme l'essai, qui part d'un levier déjà posé).
    [Fact]
    public async Task Un_levier_garde_en_session_ne_repart_pas_au_clic_suivant()
    {
        var dossier = System.IO.Directory.CreateTempSubdirectory("drdxt-session-").FullName;
        try
        {
            var m = new Machine();
            var h = Build(graines: AChaud, pid: PidDuJeu, banc: new BancReflex(m),
                journalLeviers: new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "leviers.json")),
                creer: d => new DrDxt.Application.Autopilot.TweakCandidate(new LevierReflex(m, d.Id), d, new RegistreReflex(m),
                    new DrDxt.Application.Autopilot.FichierRevertEnAttente(System.IO.Path.Combine(dossier, "revert.json"))));
            await h.Vm.ScanCommand.ExecuteAsync(null);
            h.Vm.Patienter = _ => Task.CompletedTask;

            await h.Vm.ApplyCommand.ExecuteAsync(null);
            m.Pose.Should().BeTrue("témoin : la session a gardé le levier");
            await h.Vm.ApplyCommand.ExecuteAsync(null);

            h.CandidatsCrees.Count(id => id == AChaud[0]).Should().Be(1, "gardé au premier clic, il ne repart pas au second");
            m.Pose.Should().BeTrue();
        }
        finally { System.IO.Directory.Delete(dossier, recursive: true); }
    }

    [Fact]
    public async Task Jeu_reste_reduit_la_session_n_a_pas_lieu_et_c_est_dit()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Locator!.LocateRunningGamePid().Returns((int?)null);
        h.Locator.JeuOuvert().Returns(true);
        h.Vm.Patienter = _ => Task.CompletedTask;

        await h.Vm.ApplyCommand.ExecuteAsync(null);

        h.CandidatsCrees.Should().BeEmpty("aucun levier n'est posé sur un jeu qu'on ne peut pas mesurer");
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Session.JeuReduit");
    }
}
