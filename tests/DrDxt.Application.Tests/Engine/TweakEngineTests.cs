using DrDxt.Application.Engine;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Engine;

// -----------------------------------------------------------------------
// M2 Phase C — TweakEngine (ITweakEngine), the transactional heart of M2.
//
// C1  Plan + transactional apply: Never a half-applied batch.
//     - Plan(...) gates: keeps applicable tweaks, collects skipped ids.
//     - ApplyBatch: (1) EnsureRestorePointAsync — fail ⇒ ABORT, apply nothing;
//       (2) apply each tweak, pushing its snapshot on a LIFO stack; first failure
//       ⇒ pop+RevertAsync each already-applied (LIFO) ⇒ ENGINE.BATCH_REVERTED;
//       a revert that itself fails during rollback ⇒ log critical + still return
//       BATCH_REVERTED listing the un-reverted ids (never throws).
// C2  Auto-revert on MEASURED regression: re-bench → AbComparer significance:
//       regression-beyond-margin ⇒ auto-revert whole batch (LIFO) +
//       ENGINE.REGRESSION_REVERTED + SummaryKey verdict.regression_reverted;
//       no-measurable-gain ⇒ KEPT (not a regression) + verdict.no_measurable_gain;
//       measurable gain ⇒ KEPT + reported.
//
// Pure unit tests with NSubstitute fakes — nothing elevated, no real system.
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

    // Baseline benchmark (the "before").
    private static readonly BenchmarkResult Baseline = Bench(avg: 200, p1: 150, p01: 120, margin: 2);

    /// <summary>La meme reference, mais qui declare son exposition : sans elle, aucune des deux
    /// passes ne peut dire si elles ont mesure la meme session. `null` des deux cotes reste
    /// volontairement neutre -- Unknown ne deduit rien.</summary>
    private static readonly BenchmarkResult BaselineExposee = Baseline with { HygieneKeptFraction = 1.0 };

    private static BenchmarkResult Bench(double avg, double p1, double p01, double margin) =>
        new(avg, p1, p01, new[] { 5.0, 5.0, 5.0 }, EstimatedPcLatencyMs: 10, MarginOfError: margin, Runs: 3, P1LowMarginOfError: margin);

    // --- A configurable fake tweak (so we can assert apply/revert ordering). ---
    // The engine reverts via the TWEAK's own RevertAsync (so composite-snapshot tweaks
    // are fully restored — golden rule §6), so every fake's RevertAsync returns Ok by
    // default and we assert on IT (not _safety) for the rollback / auto-revert paths.
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
        // Le snapshot porte une cible relisible depuis le 10/09 : sans elle, le moteur garde le
        // levier mais refuse de mesurer, et ces tests portent justement sur la mesure. C'est
        // l'invariant de la phase 18 — seul `Applied` ouvre le banc.
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
        // Defaults: restore point ok, all reverts ok, bench returns baseline-equal
        // (no measurable gain) unless a test overrides it.
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

    // ====================== C1 — transactional apply ======================

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
        await t1.DidNotReceive().ApplyAsync();              // nothing applied without a point
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_failure_reverts_everything_already_applied_lifo()
    {
        var t1 = FakeTweak("t1");
        var t2 = FakeTweak("t2", applyOk: false);          // fails at apply
        var engine = Build();
        Register(t1, t2);

        var r = await engine.ApplyAsync(Plan(t1, t2), Baseline, Pid, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        // t1 (the only one that applied) was reverted via ITS own RevertAsync; t2 never
        // produced a snapshot. The engine no longer routes rollback through _safety.
        await t1.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "t1"));
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        // No re-bench when the batch failed to apply cleanly.
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_failure_reverts_in_lifo_order()
    {
        var t1 = FakeTweak("t1");
        var t2 = FakeTweak("t2");
        var t3 = FakeTweak("t3", applyOk: false);          // third fails
        var engine = Build();
        Register(t1, t2, t3);

        // Order is recorded from each TWEAK's own RevertAsync (the engine's revert path).
        var reverted = new List<string>();
        t1.RevertAsync(Arg.Any<TweakSnapshot>())
            .Returns(_ => { reverted.Add("t1"); return Task.FromResult(Result.Ok()); });
        t2.RevertAsync(Arg.Any<TweakSnapshot>())
            .Returns(_ => { reverted.Add("t2"); return Task.FromResult(Result.Ok()); });

        var r = await engine.ApplyAsync(Plan(t1, t2, t3), Baseline, Pid, default);

        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        reverted.Should().Equal("t2", "t1");               // LIFO: last applied reverted first
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

        r.IsOk.Should().BeFalse();                          // exception never escapes
        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        // t1 reverted via its own RevertAsync; tBoom never produced a snapshot to revert.
        await t1.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "t1"));
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Failed_revert_during_rollback_still_returns_batch_reverted_and_logs_unreverted()
    {
        // The rollback revert of t1 itself fails — must not throw; must still report
        // BATCH_REVERTED and surface that t1 was not reverted (ultimate net = restore point).
        var t1 = FakeTweak("t1", revertOk: false);
        var t2 = FakeTweak("t2", applyOk: false);
        var engine = Build();
        Register(t1, t2);

        var r = await engine.ApplyAsync(Plan(t1, t2), Baseline, Pid, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        r.Error!.Value.Message.Should().Contain("t1");      // un-reverted id surfaced
        // Logged critical (a change record for the failed rollback).
        _diag.ReceivedWithAnyArgs().Change(default!, default!, default!, default!);
    }

    [Fact]
    public async Task Logs_each_applied_change_via_diagnostics()
    {
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        // bench returns a clear gain so the batch is kept.
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

    // ====================== C2 — auto-revert on measured regression ======================

    [Fact]
    public async Task Measured_regression_beyond_margin_auto_reverts_that_batch()
    {
        var t1 = FakeTweak("t1");
        var t2 = FakeTweak("t2");
        var engine = Build();
        Register(t1, t2);
        // after WORSE than baseline beyond combined margins (200 - 20 > 2+2).
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 180, p1: 120, p01: 100, margin: 2))));

        var r = await engine.ApplyAsync(Plan(t1, t2), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();                            // a measured regression is a known, handled outcome
        r.Value!.Verdict.Should().NotBeNull();
        r.Value.Verdict!.IsMeasurableGain.Should().BeFalse();
        r.Value.Verdict.SummaryKey.Should().Be("verdict.regression_reverted");
        // Both applied tweaks auto-reverted (LIFO) via their own RevertAsync.
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
        // The reversibility-gap regression test (golden rule §6): a composite-snapshot
        // tweak (X3D CCD fix / VRR cap) stores a multi-op snapshot that only its own
        // RevertAsync can decode. If the engine auto-reverted via _safety.RevertAsync
        // (single typed-op decode), the composite would be only PARTIALLY restored. We
        // model that here: _safety.RevertAsync FAILS for this snapshot (it can't decode
        // it), but the tweak's own RevertAsync succeeds — and the engine must use the
        // tweak's. On a faked regression the whole batch auto-reverts cleanly.
        var composite = FakeTweak("fixit.x3d_ccd");
        var engine = Build();
        Register(composite);
        // The generic safety path cannot restore a composite snapshot.
        _safety.RevertAsync(Arg.Any<TweakSnapshot>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.SnapshotInvalid, "composite not decodable by safety")));
        // after WORSE than baseline beyond combined margins → engine auto-reverts.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 180, p1: 120, p01: 100, margin: 2))));

        var r = await engine.ApplyAsync(Plan(composite), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        // The composite was FULLY auto-reverted — via its own RevertAsync, not _safety.
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
        // after within combined margins of baseline → no measurable gain.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 201, p1: 150, p01: 120, margin: 2))));

        var r = await engine.ApplyAsync(Plan(t1), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict!.IsMeasurableGain.Should().BeFalse();
        r.Value.Verdict.SummaryKey.Should().Be("verdict.no_measurable_gain");
        r.Value.Outcomes.Where(o => o.AutoReverted).Should().BeEmpty();   // no gain ≠ regression: KEEP
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied);
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Measurable_gain_is_kept_and_reported()
    {
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);
        // after clearly better than baseline beyond margins → measurable gain.
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
        // The decisive efficiency-tweak case (GPU undervolt / power-limit / fan curve):
        // avg FPS flat WITHIN the combined margin (an undervolt is ~0 on avg FPS by design)
        // but 1% lows improved BEYOND the margin (steadier frames). The OLD avg-only rule
        // would have called this "no measurable gain"; the frame-consistency criterion makes
        // it a measurable GAIN. Either way it must not be auto-reverted.
        var undervolt = FakeTweak("gpu.undervolt", category: TweakCategory.GpuTuning);
        var engine = Build();
        Register(undervolt);
        // baseline avg 200 / p1 150 (margin 2); after avg 201 (flat, within margin) but
        // p1 170 (up 20 ≫ combined margin 4) → frame consistency clearly improved.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 201, p1: 170, p01: 120, margin: 2))));

        var r = await engine.ApplyAsync(Plan(undervolt), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        // KEPT: never auto-reverted (this is a WIN, not a regression).
        r.Value!.Outcomes.Where(o => o.AutoReverted).Should().BeEmpty();
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied);
        await undervolt.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        await _safety.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        // Reported as a measurable gain (steadier 1% lows), not a regression-revert.
        r.Value.Verdict!.IsMeasurableGain.Should().BeTrue();
        r.Value.Verdict.SummaryKey.Should().Be("verdict.measurable_gain");
    }

    [Fact]
    public async Task Regression_on_1pct_lows_alone_auto_reverts_even_if_avg_flat()
    {
        // The other side of the criterion: avg FPS flat within margin but 1% lows DROP
        // beyond the margin (per-game ReBAR/undervolt frame-consistency regression). The
        // avg-only rule missed this; the frame-consistency criterion auto-reverts it.
        var t1 = FakeTweak("gpu.undervolt", category: TweakCategory.GpuTuning);
        var engine = Build();
        Register(t1);
        // baseline avg 200 / p1 150 (margin 2); after avg 200 (flat) but p1 120 (down 30).
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
        // If we cannot re-measure, we do not claim a gain and we do not auto-revert a
        // change we have no measured reason to revert — the batch stays applied with a
        // null verdict (honesty §5). The user can still revert manually / via restore.
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
        // HAGS / Game Mode etc. only take effect after a reboot / next game launch, so an
        // immediate re-bench can't reflect them and must not auto-revert them on session
        // noise. A batch made entirely of reboot-required tweaks is kept as-is, with NO
        // re-bench (the gain is proven by the next in-game scan — honest deferred proof).
        var reboot1 = FakeTweak("latency.hagsOn", takesEffectAfterReboot: true);
        var reboot2 = FakeTweak("os.gameMode", takesEffectAfterReboot: true);
        var engine = Build();
        Register(reboot1, reboot2);
        // Even if a re-bench would read a regression, the engine must not consult it here.
        _bench.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BenchmarkResult>.Ok(Bench(avg: 100, p1: 60, p01: 40, margin: 2))));

        var r = await engine.ApplyAsync(Plan(reboot1, reboot2), Baseline, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict.Should().BeNull();                       // no A/B done → no invented verdict
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied && !o.AutoReverted);
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await reboot1.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        await reboot2.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    [Fact]
    public async Task Mixed_batch_with_one_immediate_tweak_still_rebenches()
    {
        // Only an ALL-reboot batch skips the A/B. A mixed batch still re-benches — the
        // immediate tweak can be measured (heuristic ceiling: a measured regression would
        // revert the whole mixed batch; split the families if that ever bites).
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
        // Degraded mode: no baseline (PresentMon unavailable at scan) → we cannot do an
        // A/B verdict, so we keep the applied batch with no verdict (never invent a gain).
        var t1 = FakeTweak("t1");
        var engine = Build();
        Register(t1);

        var r = await engine.ApplyAsync(Plan(t1), baseline: null, Pid, default);

        r.IsOk.Should().BeTrue();
        r.Value!.Verdict.Should().BeNull();
        r.Value.Outcomes.Should().OnlyContain(o => o.Applied && !o.AutoReverted);
        await _bench.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ====================== P4 — un banc exige une cible PROUVEE ======================

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
        // La correction du 10/09 (phase 18, P0). Le moteur n'annulait que sur `NotApplied` et
        // laissait `Unknown` / `Unavailable` continuer jusqu'au banc : DrDXT pouvait donc mesurer
        // un candidat sans avoir prouve que la cible etait en place, et attribuer l'ecart d'une
        // session a un changement non verifie.
        //
        // Sans lecteur, aucune cible n'est prouvable. Le levier n'est pas condamne pour autant —
        // il est garde — mais rien ne sera mesure.
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
    /// La distinction qui a failli m'echapper. Refuser de MESURER faute de preuve supprimait
    /// aussi le filet anti-regression : un batch qui degrade la machine serait reste en place.
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
