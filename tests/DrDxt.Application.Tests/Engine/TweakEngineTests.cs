using DrDxt.Application.Engine;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Engine;

// -----------------------------------------------------------------------
// TweakEngine (ITweakEngine), le cœur transactionnel du chemin d'application.
//
// C1  Plan + application transactionnelle : jamais un lot à moitié appliqué.
//     - Plan(...) filtre : garde les leviers applicables, collecte les identifiants sautés.
//     - ApplyBatch : (1) EnsureRestorePointAsync ; échec ⇒ abandon, rien n'est appliqué ;
//       (2) appliquer chaque levier en empilant son instantané (pile LIFO) ; premier échec
//       ⇒ dépiler + RevertAsync chaque levier déjà appliqué (LIFO) ⇒ ENGINE.BATCH_REVERTED ;
//       un retour arrière qui échoue lui-même pendant le rollback ⇒ journal critique + TWEAK.PARTIALLY_APPLIED
//       avec les identifiants non annulés (jamais d'exception).
// C2  Retour arrière automatique sur régression mesurée : re-banc → significativité AbComparer :
//       régression au-delà de la marge ⇒ retour arrière de tout le lot (LIFO) +
//       rapport réussi + SummaryKey verdict.regression_reverted ;
//       aucun gain mesurable ⇒ gardé (ce n'est pas une régression) + verdict.no_measurable_gain ;
//       gain mesurable ⇒ gardé + rapporté.
//
// Tests unitaires purs avec des faux NSubstitute : rien d'élevé, aucun vrai système.
// -----------------------------------------------------------------------
public sealed class TweakEngineTests
{
    private readonly ISafetyManager _safety = Substitute.For<ISafetyManager>();
    private readonly IBenchmarkRunner _bench = Substitute.For<IBenchmarkRunner>();
    private readonly IDiagnosticsLog _diag = Substitute.For<IDiagnosticsLog>();
    private readonly ITweakCatalog _catalog = Substitute.For<ITweakCatalog>();

    private const int Pid = 4242;

    private static readonly HardwareInventory DesktopHw = new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo("RTX 4070", "NVIDIA", 12L << 30, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        ChassisType.Desktop);

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    // Banc de départ (l'« avant »).
    private static readonly BenchmarkResult Baseline = Bench(avg: 200, p1: 150, p01: 120, margin: 2);

    /// <summary>La meme reference, mais qui declare son exposition : sans elle, aucune des deux
    /// passes ne peut dire si elles ont mesure la meme session. `null` des deux cotes reste
    /// volontairement neutre -- Unknown ne deduit rien.</summary>
    private static readonly BenchmarkResult BaselineExposee = Baseline with { HygieneKeptFraction = 1.0 };

    private static BenchmarkResult Bench(double avg, double p1, double p01, double margin) =>
        new(avg, p1, p01, new[] { 5.0, 5.0, 5.0 }, EstimatedPcLatencyMs: 10, MarginOfError: margin, Runs: 3, P1LowMarginOfError: margin);

    // --- Un faux levier configurable (pour vérifier l'ordre apply/revert). ---
    // Le moteur annule via le RevertAsync du levier lui-même (pour que les leviers à instantané
    // composite soient entièrement restaurés) ; chaque faux rend donc Ok par défaut sur
    // RevertAsync et c'est lui (pas _safety) qu'on vérifie sur les chemins de rollback et de retour automatique.
    private static ITweak FakeTweak(
        string id,
        bool applies = true,
        bool applyOk = true,
        bool revertOk = true,
        TweakCategory category = TweakCategory.Latency,
        bool takesEffectAfterReboot = false)
    {
        var t = Substitute.For<ITweak>();
        t.Id.Returns(id);
        t.Category.Returns(category);
        t.RequiresElevation.Returns(true);
        t.RequiresRestorePoint.Returns(true);
        t.TakesEffectAfterReboot.Returns(takesEffectAfterReboot);
        t.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(applies);
        // Le snapshot porte une cible relisible : sans elle, le moteur garde le levier mais refuse
        // de mesurer, et ces tests portent justement sur la mesure. Seul `Applied` ouvre le banc.
        t.ApplyAsync().Returns(applyOk
            ? Task.FromResult(Result<TweakSnapshot>.Ok(SnapshotRelisible(id)))
            : Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.HelperOpFailed, "apply failed")));
        t.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(revertOk
            ? Task.FromResult(Result.Ok())
            : Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "revert failed")));
        return t;
    }

    /// <summary>Un snapshot dont la cible (« 2 ») est relisible : `LecteurQuiProuve` la confirme,
    /// donc le moteur a le droit de mesurer.</summary>
    private static TweakSnapshot SnapshotRelisible(string id)
    {
        var inverse = DrDxt.Core.PrivilegedOpCodec.Serialize(
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\Test", "Feuille",
                RegistryValueType.DWord, "1"));
        var cible = DrDxt.Core.PrivilegedOpCodec.Serialize(
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\Test", "Feuille",
                RegistryValueType.DWord, "2"));
        return new TweakSnapshot(id, inverse.Value!, cible.Value!);
    }

    private TweakEngine Build()
    {
        // Par défaut : point de restauration ok, tous les retours ok, le banc rend l'égal du
        // départ (aucun gain mesurable) sauf si un test le redéfinit.
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));
        _safety.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Task.FromResult(Result.Ok()));
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Baseline)));
        // Le lecteur voit la cible en place : les tests de mesure portent sur un levier prouve
        // applique, ce qui est desormais la seule facon d'atteindre le banc.
        return new TweakEngine(_safety, _bench, _diag, _catalog, new LecteurFige(2));
    }

    private void Register(params ITweak[] tweaks)
    {
        _catalog.All().Returns(tweaks);
        foreach (var t in tweaks)
            _catalog.Get(t.Id).Returns(Result<ITweak>.Ok(t));
    }

    private static TweakPlan Plan(params ITweak[] tweaks) => new(tweaks.Select(t => t.Id).ToArray());

    // ====================== C1 — gating ======================

    [Fact]
    public void Plan_skips_non_applicable_tweaks()
    {
        var laptopUnsafe = FakeTweak("gpu.undervolt", applies: false);
        var generic = FakeTweak("latency.reflex", applies: true);
        var engine = Build();
        Register(laptopUnsafe, generic);

        var plan = engine.Plan(new[] { laptopUnsafe, generic }, DesktopHw, State);

        plan.Applicable.Select(t => t.Id).Should().ContainSingle().Which.Should().Be("latency.reflex");
        plan.SkippedIds.Should().Contain("gpu.undervolt");
    }

    // ====================== C1 : application transactionnelle ======================

    // Le garde de frontière rendait ENGINE.BATCH_REVERTED sur une exception levée après une pose, sans rien rendre : la pile
    // des leviers posés vivait dans la méthode qui venait de lever. L'App croyait le lot défait, les leviers restaient posés.
    [Fact]
    public async Task Une_exception_apres_une_pose_rend_ce_qui_a_ete_pose()
    {
        var a = FakeTweak("a");
        var b = FakeTweak("b");
        var engine = Build();
        Register(a, b);
        _diag.When(d => d.Change("b", Arg.Any<string>(), "applied", Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("journal en panne"));

        var r = await engine.ApplyAsync(Plan(a, b), baseline: null, Pid, CancellationToken.None);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        await b.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
        await a.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task RestorePoint_failure_aborts_before_any_apply()
    {
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        _safety.EnsureRestorePointAsync()
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.RestoreDisabled, "off")));

        var r = await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RestoreDisabled);
        await t1.DidNotReceive().ApplyAsync();              // rien n'est appliqué sans point
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Le helper a reçu l'écriture, l'a notée à son journal, puis s'est tu : le réglage a pu être posé. Par le vrai moteur et
    // un vrai levier : le lot ne se dit jamais « défait » (l'écran dirait « rien n'est resté »), il est partiellement posé.
    [Fact]
    public async Task Un_helper_qui_ecrit_puis_se_tait_ne_rend_jamais_un_lot_defait()
    {
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")
            .Returns(Result<uint?>.Ok(1u));
        var eleve = Substitute.For<IElevatedClient>();
        eleve.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperNoReplyJournalisee, "aucune réponse après l'envoi")));
        var hags = new DrDxt.Application.Tweaks.Latency.HagsOnTweak(registre, eleve);
        var engine = Build();
        Register(hags);

        var r = await engine.ApplyAsync(Plan(hags), Baseline, Pid, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied, "le réglage a pu rester posé");
    }

    // Un levier qui dit lui-même n'avoir pas pu revenir (l'écran resté au mode cible) : le lot n'est pas « défait ».
    [Fact]
    public async Task Un_levier_reste_partiellement_pose_ne_rend_jamais_un_lot_defait()
    {
        var t = FakeTweak("ecran");
        t.ApplyAsync().Returns(Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.TweakPartiallyApplied, "pas revenu")));
        var engine = Build();
        Register(t);

        var r = await engine.ApplyAsync(Plan(t), Baseline, Pid, default);

        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
    }

    [Fact]
    public async Task Batch_failure_reverts_everything_already_applied_lifo()
    {
        var t1 = FakeTweak("t1");
        var t2 = FakeTweak("t2", applyOk: false);          // échoue à l'application
        var engine = Build();
        Register(t1, t2);

        var r = await engine.ApplyAsync(Plan(t1, t2), Baseline, Pid, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        // t1 (le seul appliqué) a été annulé via son propre RevertAsync ; t2 n'a jamais
        // produit d'instantané. Le moteur ne fait plus passer le rollback par _safety.
        await t1.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "t1"));
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        // Pas de re-banc quand le lot n'a pas pu s'appliquer proprement.
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_failure_reverts_in_lifo_order()
    {
        var t1 = FakeTweak("t1");
        var t2 = FakeTweak("t2");
        var t3 = FakeTweak("t3", applyOk: false);          // le troisième échoue
        var engine = Build();
        Register(t1, t2, t3);

        // L'ordre est relevé sur le RevertAsync de chaque levier (le chemin de retour du moteur).
        var reverted = new List<string>();
        t1.RevertAsync(Arg.Any<TweakSnapshot>())
            .Returns(_ => { reverted.Add("t1"); return Task.FromResult(Result.Ok()); });
        t2.RevertAsync(Arg.Any<TweakSnapshot>())
            .Returns(_ => { reverted.Add("t2"); return Task.FromResult(Result.Ok()); });

        var r = await engine.ApplyAsync(Plan(t1, t2, t3), Baseline, Pid, default);

        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        reverted.Should().Equal("t2", "t1");               // LIFO : le dernier appliqué est annulé en premier
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Apply_throwing_tweak_is_mapped_to_failure_and_batch_reverts()
    {
        var t1 = FakeTweak("t1");
        var tBoom = Substitute.For<ITweak>();
        tBoom.Id.Returns("boom");
        tBoom.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(true);
        tBoom.ApplyAsync().Returns<Task<Result<TweakSnapshot>>>(_ => throw new InvalidOperationException("kaboom"));
        var engine = Build();
        Register(t1, tBoom);

        var r = await engine.ApplyAsync(Plan(t1, tBoom), Baseline, Pid, default);

        r.IsOk.Should().BeFalse();                          // l'exception ne s'échappe jamais
        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        // t1 annulé via son propre RevertAsync ; tBoom n'a jamais produit d'instantané à annuler.
        await t1.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "t1"));
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Failed_revert_during_rollback_returns_partially_applied_and_logs_unreverted()
    {
        // Le retour arrière de rollback de t1 échoue lui-même : pas d'exception. t1 est resté posé : le lot n'est pas
        // « défait » (BATCH_REVERTED ferait dire à l'écran le système inchangé) ; t1 est nommé (filet ultime = point de restauration).
        var t1 = FakeTweak("t1", revertOk: false);
        var t2 = FakeTweak("t2", applyOk: false);
        var engine = Build();
        Register(t1, t2);

        var r = await engine.ApplyAsync(Plan(t1, t2), Baseline, Pid, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
        r.Error!.Value.Message.Should().Contain("t1");      // un-reverted id surfaced
        // Journal critique (une trace de changement pour le rollback raté).
        _diag.ReceivedWithAnyArgs().Change(default!, default!, default!, default!);
    }

    [Fact]
    public async Task Logs_each_applied_change_via_diagnostics()
    {
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        // le banc rend un gain net, donc le lot est gardé.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 230, p1: 180, p01: 150, margin: 2))));

        await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        _diag.Received().Change("t1", Arg.Any<string>(), Arg.Any<string>(), "DrDXT");
    }

    [Fact]
    public async Task Empty_plan_applies_nothing_and_does_not_create_restore_point()
    {
        var engine = Build();
        Register();

        var r = await engine.ApplyAsync(new TweakPlan(Array.Empty<string>()), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Outcomes.Should().BeEmpty();
        await _safety.DidNotReceive().EnsureRestorePointAsync();
    }

    [Fact]
    public async Task Plan_id_not_in_catalog_is_skipped_cleanly()
    {
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        _catalog.Get("ghost").Returns(Result<ITweak>.Fail(ErrorCodes.OpNotAllowed, "unknown"));
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 230, p1: 180, p01: 150, margin: 2))));

        var r = await engine.ApplyAsync(new TweakPlan(new[] { "t1", "ghost" }), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Outcomes.Select(o => o.TweakId).Should().Contain("t1").And.NotContain("ghost");
    }

    // ====================== C2 : retour automatique sur régression mesurée ======================

    [Fact]
    public async Task Measured_regression_beyond_margin_auto_reverts_that_batch()
    {
        var t1 = FakeTweak("t1");
        var t2 = FakeTweak("t2");
        var engine = Build();
        Register(t1, t2);
        // après pire que le départ au-delà des marges combinées (200 - 20 > 2+2).
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 180, p1: 120, p01: 100, margin: 2))));

        var r = await engine.ApplyAsync(Plan(t1, t2), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();                            // une régression mesurée est une issue connue et traitée
        r.Value!.Verdict.Should().NotBeNull();
        r.Value.Verdict!.IsMeasurableGain.Should().BeFalse();
        r.Value.Verdict.SummaryKey.Should().Be("verdict.regression_reverted");
        // Les deux leviers appliqués annulés automatiquement (LIFO) via leur propre RevertAsync.
        r.Value.Outcomes.Where(o => o.AutoReverted).Select(o => o.TweakId)
            .Should().BeEquivalentTo(new[] { "t1", "t2" });
        await t1.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "t1"));
        await t2.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "t2"));
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Une_comparaison_INVALIDE_n_annule_RIEN_du_tout()
    {
        // Le moteur annulait un changement sur la foi d'une comparaison qui ne tient pas.
        // Ici la passe apres a perdu 45 % de ses trames (le joueur a alt-tabbe) quand la passe
        // Avant n'en a perdu aucune : les deux n'ont pas mesure la meme session. Les FPS sont
        // effondres, `AbComparer.Compare` crie a la regression -- et annuler serait defaire un
        // reglage peut-etre bon a cause d'un alt-tab.
        //
        // C'est exactement la distinction que le modele de preuve apporte : « je n'ai rien vu »
        // et « je n'ai pas mesure » ne se traitent pas pareil. Une experience invalide se conclut
        // comme une re-mesure impossible : on garde, et on n'affirme rien.
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(
                Bench(avg: 120, p1: 80, p01: 60, margin: 2) with { HygieneKeptFraction = 0.55 })));

        var r = await engine.ApplyAsync(Plan(t1), BaselineExposee, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict.Should().BeNull("une experience qui ne tient pas ne rend aucun verdict");
        r.Value.Outcomes.Should().OnlyContain(o => !o.AutoReverted,
            "on n'annule pas un reglage a cause d'un alt-tab");
        await t1.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Une_regression_reste_annulee_quand_les_deux_passes_sont_comparables()
    {
        // Le controle qui empeche la garde precedente d'etre une echappatoire : memes chiffres,
        // meme effondrement, mais les deux passes ont la meme exposition. La regression tient,
        // et l'auto-annulation doit se declencher comme avant.
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(
                Bench(avg: 120, p1: 80, p01: 60, margin: 2) with { HygieneKeptFraction = 1.0 })));

        var r = await engine.ApplyAsync(Plan(t1), BaselineExposee, Pid, default);

        r.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        await t1.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Composite_snapshot_tweak_is_auto_reverted_via_its_OWN_revert_not_safety()
    {
        // Le test de régression sur le trou de réversibilité : un levier à instantané composite
        // (correctif CCD X3D, plafond VRR) stocke un instantané multi-ops que seul son propre
        // RevertAsync sait décoder. Si le moteur annulait via _safety.RevertAsync (décodage d'une
        // seule op typée), le composite ne serait restauré qu'en partie. Modélisé ici :
        // _safety.RevertAsync échoue sur cet instantané (il ne sait pas le décoder), mais le
        // RevertAsync du levier réussit, et le moteur doit utiliser celui du levier. Sur une
        // régression simulée, tout le lot revient en arrière proprement.
        var composite = FakeTweak("fixit.x3d_ccd");
        var engine = Build();
        Register(composite);
        // Le chemin de sécurité générique ne sait pas restaurer un instantané composite.
        _safety.RevertAsync(Arg.Any<TweakSnapshot>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.SnapshotInvalid, "composite not decodable by safety")));
        // après pire que le départ au-delà des marges combinées → le moteur annule tout seul.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 180, p1: 120, p01: 100, margin: 2))));

        var r = await engine.ApplyAsync(Plan(composite), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        // Le composite a été entièrement annulé, via son propre RevertAsync, pas _safety.
        r.Value.Outcomes.Should().ContainSingle()
            .Which.Should().Match<ApplyOutcome>(o => o.TweakId == "fixit.x3d_ccd" && o.AutoReverted);
        await composite.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "fixit.x3d_ccd"));
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task No_measurable_gain_is_kept_but_reported_honestly()
    {
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        // après dans les marges combinées du départ → aucun gain mesurable.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 201, p1: 150, p01: 120, margin: 2))));

        var r = await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict!.IsMeasurableGain.Should().BeFalse();
        r.Value.Verdict.SummaryKey.Should().Be("verdict.no_measurable_gain");
        r.Value.Outcomes.Where(o => o.AutoReverted).Should().BeEmpty();   // pas de gain ≠ régression : garder
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied);
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Measurable_gain_is_kept_and_reported()
    {
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        // après nettement meilleur que le départ au-delà des marges → gain mesurable.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 230, p1: 190, p01: 160, margin: 2))));

        var r = await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict!.IsMeasurableGain.Should().BeTrue();
        r.Value.Verdict.SummaryKey.Should().Be("verdict.measurable_gain");
        r.Value.Outcomes.Where(o => o.AutoReverted).Should().BeEmpty();
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied);
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Efficiency_tweak_with_flat_avg_and_better_1pct_lows_is_kept_not_reverted()
    {
        // Le cas décisif des leviers d'efficacité (undervolt GPU, limite de puissance, courbe de ventilation) :
        // FPS moyens plats dans la marge combinée (un undervolt vaut ~0 sur la moyenne par construction)
        // mais 1 % bas améliorés au-delà de la marge (images plus régulières). Une règle sur la seule
        // moyenne dirait « aucun gain mesurable » ; le critère de régularité des images en fait
        // un gain mesurable. Dans les deux cas, pas de retour arrière automatique.
        var undervolt = FakeTweak("gpu.undervolt", category: TweakCategory.GpuTuning);
        var engine = Build();
        Register(undervolt);
        // départ moy. 200 / p1 150 (marge 2) ; après moy. 201 (plat, dans la marge) mais
        // p1 170 (+20 ≫ marge combinée 4) → régularité des images nettement meilleure.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 201, p1: 170, p01: 120, margin: 2))));

        var r = await engine.ApplyAsync(Plan(undervolt), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        // Gardé : jamais annulé automatiquement (c'est un gain, pas une régression).
        r.Value!.Outcomes.Where(o => o.AutoReverted).Should().BeEmpty();
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied);
        await undervolt.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        // Rapporté comme gain mesurable (1 % bas plus réguliers), pas comme régression annulée.
        r.Value.Verdict!.IsMeasurableGain.Should().BeTrue();
        r.Value.Verdict.SummaryKey.Should().Be("verdict.measurable_gain");
    }

    // Un retour automatique raté ne doit pas être tracé qu'au journal : l'écran dirait « changements annulés
    // automatiquement » et rangerait le levier, toujours posé et qui dégrade, parmi les changements gardés.
    [Fact]
    public async Task A_failed_auto_revert_is_said_failed_not_reverted()
    {
        var t1 = FakeTweak("gpu.undervolt", category: TweakCategory.GpuTuning, revertOk: false);
        var engine = Build();
        Register(t1);
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 200, p1: 120, p01: 100, margin: 2))));

        var r = await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        var o = r.Value!.Outcomes.Single();
        o.AutoReverted.Should().BeFalse();
        o.Error.Should().NotBeNull("le retour a échoué : le levier est toujours posé");
        r.Value.Verdict!.SummaryKey.Should().Be("verdict.regression_revert_failed");
    }

    [Fact]
    public async Task Regression_on_1pct_lows_alone_auto_reverts_even_if_avg_flat()
    {
        // L'autre face du critère : FPS moyens plats dans la marge mais 1 % bas qui chutent
        // au-delà (régression de régularité par jeu, ReBAR ou undervolt). La règle sur la seule
        // moyenne la manquait ; le critère de régularité des images l'annule automatiquement.
        var t1 = FakeTweak("gpu.undervolt", category: TweakCategory.GpuTuning);
        var engine = Build();
        Register(t1);
        // départ moy. 200 / p1 150 (marge 2) ; après moy. 200 (plat) mais p1 120 (-30).
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 200, p1: 120, p01: 100, margin: 2))));

        var r = await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        r.Value.Outcomes.Where(o => o.AutoReverted).Select(o => o.TweakId)
            .Should().BeEquivalentTo(new[] { "gpu.undervolt" });
        await t1.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "gpu.undervolt"));
    }

    [Fact]
    public async Task Rebench_failure_keeps_batch_and_reports_no_verdict_honestly()
    {
        // Si on ne peut pas remesurer, on n'annonce pas de gain et on n'annule pas un changement
        // qu'aucune mesure ne justifie d'annuler : le lot reste appliqué avec un verdict nul.
        // Le joueur peut toujours annuler à la main ou par la restauration.
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Fail(ErrorCodes.BenchmarkFailed, "no frames")));

        var r = await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict.Should().BeNull();
        r.Value.Outcomes.Where(o => o.AutoReverted).Should().BeEmpty();
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Reboot_required_batch_is_kept_without_rebench_even_with_a_baseline()
    {
        // HAGS, Mode Jeu, etc. ne prennent effet qu'après redémarrage ou au prochain lancement de jeu :
        // un re-banc immédiat ne peut pas les refléter et ne doit pas les annuler sur le bruit de la
        // session. Un lot fait uniquement de leviers à redémarrage est gardé tel quel, sans
        // re-banc (le gain se prouve au prochain scan en jeu : preuve différée, honnête).
        var reboot1 = FakeTweak("latency.hagsOn", takesEffectAfterReboot: true);
        var reboot2 = FakeTweak("os.gameMode", takesEffectAfterReboot: true);
        var engine = Build();
        Register(reboot1, reboot2);
        // Même si un re-banc lirait une régression, le moteur ne doit pas le consulter ici.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 100, p1: 60, p01: 40, margin: 2))));

        var r = await engine.ApplyAsync(Plan(reboot1, reboot2), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict.Should().BeNull();                       // pas d'A/B → pas de verdict inventé
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied && !o.AutoReverted);
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await reboot1.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        await reboot2.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Mixed_batch_with_one_immediate_tweak_still_rebenches()
    {
        // Seul un lot entièrement à redémarrage saute l'A/B. Un lot mixte re-bance : le levier
        // immédiat se mesure (plafond de l'heuristique : une régression mesurée annulerait
        // tout le lot mixte ; séparer les familles si ça mord un jour).
        var immediate = FakeTweak("gpu.undervolt", category: TweakCategory.GpuTuning);
        var reboot = FakeTweak("latency.hagsOn", takesEffectAfterReboot: true);
        var engine = Build();
        Register(immediate, reboot);

        await engine.ApplyAsync(Plan(immediate, reboot), Baseline, Pid, default);

        await _bench.Received().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_baseline_skips_rebench_and_keeps_batch()
    {
        // Mode dégradé : pas de départ (PresentMon indisponible au scan) → pas de verdict A/B
        // possible, donc on garde le lot appliqué sans verdict (jamais inventer un gain).
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);

        var r = await engine.ApplyAsync(Plan(t1), baseline: null, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict.Should().BeNull();
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied && !o.AutoReverted);
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ====================== Un banc exige une cible PROUVEE ======================

    /// <summary>Un levier de registre dont l'apply « reussit » et dont on choisit ce que la
    /// relecture verra.</summary>
    private static ITweak LevierRegistre(string id, string avant, string demande)
    {
        var chemin = @"SYSTEM\Test";
        var inverse = DrDxt.Core.PrivilegedOpCodec.Serialize(
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, chemin, "Feuille",
                RegistryValueType.DWord, avant));
        var cible = DrDxt.Core.PrivilegedOpCodec.Serialize(
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, chemin, "Feuille",
                RegistryValueType.DWord, demande));

        var t = Substitute.For<ITweak>();
        t.Id.Returns(id);
        t.Category.Returns(TweakCategory.Latency);
        t.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(true);
        t.ApplyAsync().Returns(Task.FromResult(Result<TweakSnapshot>.Ok(
            new TweakSnapshot(id, inverse.Value!, cible.Value!))));
        t.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Task.FromResult(Result.Ok()));
        return t;
    }

    // Un levier posé sans mesure (le plafond de chaleur du processeur) se relit par le processeur lui-même : sans ses
    // lecteurs, le moteur le garderait non relu, et le résultat le dirait « effectif tout de suite ».
    private const ulong Unite = 0x3, Registre65W = (1752UL << 32) | (1UL << 47) | (1UL << 15) | 520UL, Info65W = 520UL;
    private static readonly ulong Pose = (Registre65W & ~0x7FFFUL) | 400UL;

    private static ICpuPowerLimitReader Processeur(ulong pl)
    {
        var cpu = Substitute.For<ICpuPowerLimitReader>();
        cpu.LireRegistres().Returns(Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(new RegistresCpuLus(Unite, pl, Info65W),
            RegistreDeLimiteCpu.Decoder(Unite, pl, Info65W)! with { CopieMmioPl1W = 0, ChaleurEnPartieMaxC = 80 })));
        return cpu;
    }

    private ITweak PlafondDeChaleur()
    {
        var t = FakeTweak("fixit.cpu_power_cap");
        t.ApplyAsync().Returns(Result<TweakSnapshot>.Ok(new TweakSnapshot("fixit.cpu_power_cap",
            DrDxt.Core.PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(Registre65W, Pose)).Value!,
            DrDxt.Core.PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(Pose, Registre65W)).Value!)));
        return t;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_levier_du_processeur_se_relit_par_le_processeur_dans_le_lot(bool pris)
    {
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));
        var t = PlafondDeChaleur();
        Register(t);
        var engine = new TweakEngine(_safety, _bench, _diag, _catalog, new LecteurFige(2), cpu: Processeur(pris ? Pose : Registre65W));

        await engine.ApplyAsync(Plan(t), Baseline, Pid, default);

        if (pris) _diag.Received().Change("fixit.cpu_power_cap", Arg.Any<string>(), "readback:Applied", "DrDXT");
        else await t.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
    }

    private sealed class LecteurFige(uint? valeur) : DrDxt.Application.Tweaks.Hardware.IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
            => Result<uint?>.Ok(valeur);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);
    }

    /// <summary>
    /// Le defaut que ce test ferme. Un `Result.Ok` de l'helper dit que l'op a ete ENVOYEE, pas
    /// qu'elle a PRIS : une feuille protegee par une strategie de groupe, un pilote qui clampe ou
    /// un autre outil qui reecrit derriere donnent tous un apply « reussi » et une valeur cible
    /// absente. Le moteur mesurait alors l'ecart d'une session et l'attribuait a un changement
    /// inexistant — c'est exactement ainsi qu'on fabrique un faux gain.
    /// </summary>
    [Fact]
    public async Task Un_levier_qui_n_a_PAS_pris_est_annule_et_AUCUN_banc_ne_tourne()
    {
        var t = LevierRegistre("latency.test", avant: "1", demande: "2");
        Register(t);
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Baseline)));

        // La relecture voit la valeur d'avant : l'ecriture n'a pas pris.
        var moteur = new TweakEngine(_safety, _bench, _diag, _catalog, new LecteurFige(1));

        var r = await moteur.ApplyAsync(Plan(t), BaselineExposee, Pid, CancellationToken.None);

        r.IsOk.Should().BeFalse("un levier non applique ne peut pas etre garde");
        r.Error!.Value.Message.Should().Contain("n'a pas pris");
        await t.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Un_levier_dont_la_CIBLE_est_en_place_va_jusqu_au_banc()
    {
        // Le contre-controle : une garde qui refuserait tout ne vaudrait pas mieux qu'une garde
        // absente.
        var t = LevierRegistre("latency.test", avant: "1", demande: "2");
        Register(t);
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Baseline)));

        var moteur = new TweakEngine(_safety, _bench, _diag, _catalog, new LecteurFige(2));

        var r = await moteur.ApplyAsync(Plan(t), BaselineExposee, Pid, CancellationToken.None);

        r.IsOk.Should().BeTrue();
        await _bench.Received().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sans_lecteur_le_batch_est_GARDE_mais_AUCUN_GAIN_n_est_annonce()
    {
        // `Unknown` / `Unavailable` n'ouvrent pas le banc, pas seulement `NotApplied` : sinon DrDXT
        // mesurerait un candidat sans avoir prouve que la cible est en place, et attribuerait l'ecart
        // d'une session a un changement non verifie.
        //
        // Sans lecteur, aucune cible n'est prouvable. Le levier n'est pas condamne pour autant :
        // il est garde, mais rien ne sera mesure.
        var t = LevierRegistre("latency.test", avant: "1", demande: "2");
        Register(t);
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));
        // Une passe apres meilleure que la reference : le gain existe, il n'est simplement
        // attribuable a personne.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(
                Bench(avg: 260, p1: 210, p01: 180, margin: 2) with { HygieneKeptFraction = 1.0 })));
        var moteur = new TweakEngine(_safety, _bench, _diag, _catalog);   // aucun lecteur

        var r = await moteur.ApplyAsync(Plan(t), BaselineExposee, Pid, CancellationToken.None);

        r.IsOk.Should().BeTrue("le levier peut fonctionner : on ne l'accuse pas");
        r.Value!.Verdict.Should().BeNull("aucun GAIN ne peut lui etre attribue");
    }

    [Fact]
    public async Task Une_cible_INCONNUE_garde_le_levier_et_n_annonce_aucun_gain()
    {
        // Snapshot ancien, sans valeur demandee : lisible pour le revert, inutilisable comme
        // preuve. `Unknown` n'est pas `Applied`.
        var inverse = DrDxt.Core.PrivilegedOpCodec.Serialize(
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\Test", "Feuille",
                RegistryValueType.DWord, "1"));
        var t = Substitute.For<ITweak>();
        t.Id.Returns("latency.ancien");
        t.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(true);
        t.ApplyAsync().Returns(Task.FromResult(Result<TweakSnapshot>.Ok(
            new TweakSnapshot("latency.ancien", inverse.Value!))));
        t.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Task.FromResult(Result.Ok()));
        Register(t);
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(
                Bench(avg: 260, p1: 210, p01: 180, margin: 2) with { HygieneKeptFraction = 1.0 })));

        var r = await new TweakEngine(_safety, _bench, _diag, _catalog, new LecteurFige(2))
            .ApplyAsync(Plan(t), BaselineExposee, Pid, CancellationToken.None);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict.Should().BeNull("« inconnu » n'attribue rien");
    }

    /// <summary>
    /// Refuser de MESURER faute de preuve supprimerait aussi le filet anti-regression : un batch qui degrade la machine
    /// resterait en place.
    /// Mesurer sert deux choses opposees — PROTEGER (annuler une regression, aucune attribution)
    /// et AFFIRMER (annoncer un gain, attribution a ce levier). Seule la seconde exige la preuve.
    /// </summary>
    [Fact]
    public async Task Une_REGRESSION_est_annulee_meme_sans_preuve_d_application()
    {
        var t = LevierRegistre("latency.test", avant: "1", demande: "2");
        Register(t);
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));
        // Une passe apres nettement pire que la reference.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(
                Bench(avg: 120, p1: 80, p01: 60, margin: 2) with { HygieneKeptFraction = 1.0 })));

        // Aucun lecteur : la cible n'est pas prouvable.
        var r = await new TweakEngine(_safety, _bench, _diag, _catalog)
            .ApplyAsync(Plan(t), BaselineExposee, Pid, CancellationToken.None);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted",
            "proteger ne demande aucune attribution");
        await t.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Une_TROISIEME_valeur_annule_le_batch_comme_un_echec()
    {
        // `Drifted` : la feuille ne porte ni la cible ni l'etat d'avant. Une autre source ecrit
        // dessus — garder le levier reviendrait a se battre en silence avec elle.
        var t = LevierRegistre("latency.test", avant: "1", demande: "2");
        Register(t);
        _safety.EnsureRestorePointAsync().Returns(Task.FromResult(Result.Ok()));

        var r = await new TweakEngine(_safety, _bench, _diag, _catalog, new LecteurFige(7))
            .ApplyAsync(Plan(t), BaselineExposee, Pid, CancellationToken.None);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("une autre source écrit");
        await t.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }
}
