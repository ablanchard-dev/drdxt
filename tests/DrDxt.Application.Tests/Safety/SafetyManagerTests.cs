using DrDxt.Application.Safety;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Safety;

// -----------------------------------------------------------------------
// SafetyManager (ISafetyManager).
//
// B1 EnsureRestorePointAsync : rien ne s'applique sans point de restauration vérifié.
//    Restauration désactivée + refusée → SAFETY.RESTORE_DECLINED ; désactivée + acceptée mais
//    impossible à activer → SAFETY.RESTORE_DISABLED ; active (ou activée) → création via le
//    client élevé ; échec de création ou de vérification → SAFETY.RESTORE_FAILED. Limité à
//    un point par session.
// B2 RevertAsync(instantané) : reconstruire la PrivilegedOp typée inverse depuis
//    l'OriginalValueJson typé de l'instantané et l'envoyer par IElevatedClient ;
//    JSON malformé → SAFETY.SNAPSHOT_INVALID (jamais d'exception).
//
// Tests unitaires purs avec des faux NSubstitute : rien d'élevé, aucun vrai système.
// -----------------------------------------------------------------------
public sealed class SafetyManagerTests
{
    private readonly IRestorePointService _restore = Substitute.For<IRestorePointService>();
    private readonly IConsentPrompt _consent = Substitute.For<IConsentPrompt>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private SafetyManager Build()
    {
        // Par défaut : le client élevé réussit, la vérification passe. Chaque test
        // redéfinit ce qui le concerne.
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // Par defaut, un point NEUF (cree maintenant, donc apres la demande).
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns(_ => null, _ => (DateTime?)DateTime.UtcNow);
        return new SafetyManager(_restore, _consent, _elevated, _ => Task.CompletedTask);
    }

    // --- B1 ---

    [Fact]
    public async Task Blocks_when_system_restore_disabled_and_cannot_enable()
    {
        _restore.IsEnabled().Returns(false);
        _consent.AskEnableRestore().Returns(true);   // le joueur accepte…
        _restore.TryEnable().Returns(false);         // …mais impossible à activer (GPO/édition)

        var r = await Build().EnsureRestorePointAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RestoreDisabled);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // Une stratégie de Windows (DisableSR) interdit la restauration : rien à demander au joueur, qui ne peut pas l'activer, et
    // le refus dit pourquoi.
    [Fact]
    public async Task Une_restauration_interdite_par_strategie_se_dit_sans_rien_demander()
    {
        _restore.IsEnabled().Returns(false);
        _restore.InterditeParStrategie().Returns(true);

        var r = await Build().EnsureRestorePointAsync();

        r.Error!.Value.Code.Should().Be(ErrorCodes.RestorePolicy);
        _consent.DidNotReceive().AskEnableRestore();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blocks_when_user_declines_enabling_restore()
    {
        _restore.IsEnabled().Returns(false);
        _consent.AskEnableRestore().Returns(false);  // refus explicite

        var r = await Build().EnsureRestorePointAsync();

        r.Error!.Value.Code.Should().Be(ErrorCodes.RestoreDeclined);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Le_point_porte_le_nom_fixe_de_DrDXT()
    {
        _restore.IsEnabled().Returns(true);

        await Build().EnsureRestorePointAsync();

        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<CreateRestorePointOp>(o => o.Description == CreateRestorePointOp.NomDrDxt), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creates_point_via_elevated_helper_when_enabled()
    {
        _restore.IsEnabled().Returns(true);

        var r = await Build().EnsureRestorePointAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_point_is_looked_for_in_the_24h_window_Windows_refuses_to_create_in()
    {
        // Windows saute la creation s'il existe un point de moins de 24 h (Microsoft Learn).
        // La fenetre part donc 24 h avant la demande, pas de la demande elle-meme.
        _restore.IsEnabled().Returns(true);
        var manager = Build();
        var avant = DateTime.UtcNow;

        await manager.EnsureRestorePointAsync();

        // Cherché avant de créer (réutiliser), puis après (vérifier).
        _restore.Received(2).DernierPointDepuis(Arg.Is<DateTime>(d =>
            d >= avant - SafetyManager.FenetreDuRefusDeWindows
            && d <= DateTime.UtcNow - SafetyManager.FenetreDuRefusDeWindows));
    }

    [Fact]
    public async Task A_point_Windows_kept_from_earlier_today_protects_and_is_said()
    {
        // Un 2e « Optimiser » dans la journee : Windows garde le point de ce matin. On ne bloque pas.
        _restore.IsEnabled().Returns(true);
        var manager = Build();
        var ceMatin = DateTime.UtcNow.AddHours(-10);
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns(ceMatin);

        var r = await manager.EnsureRestorePointAsync();

        r.IsOk.Should().BeTrue("un point de moins de 24 h date d'avant nos changements : il protege");
        manager.PointReutiliseUtc.Should().Be(ceMatin, "l'ecran doit pouvoir dire de quand date le point");
        // Pas un point par « Optimiser » ; celui de moins de 24 h suffit.
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_fresh_point_is_not_reported_as_reused()
    {
        _restore.IsEnabled().Returns(true);
        var manager = Build();

        (await manager.EnsureRestorePointAsync()).IsOk.Should().BeTrue();

        manager.PointReutiliseUtc.Should().BeNull();
    }

    [Fact]
    public async Task Enables_then_creates_when_user_accepts()
    {
        _restore.IsEnabled().Returns(false);
        _consent.AskEnableRestore().Returns(true);
        _restore.TryEnable().Returns(true);

        var r = await Build().EnsureRestorePointAsync();

        r.IsOk.Should().BeTrue();
        _restore.Received(1).TryEnable();
        await _elevated.Received(1).ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestorePoint_creation_failure_blocks_and_does_not_proceed()
    {
        _restore.IsEnabled().Returns(true);
        var mgr = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "boom")));

        var r = await mgr.EnsureRestorePointAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RestoreFailed);
    }

    // Le retour de Windows ne prouve pas le point (recherche point-de-restauration.md) ; la
    // liste est relue plusieurs fois avant de conclure.
    [Fact]
    public async Task Un_point_qui_apparait_apres_quelques_lectures_suffit()
    {
        _restore.IsEnabled().Returns(true);
        var attentes = 0;
        var mgr = new SafetyManager(_restore, _consent, _elevated, _ => { attentes++; return Task.CompletedTask; });
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns(null, null, null, (DateTime?)DateTime.UtcNow);

        (await mgr.EnsureRestorePointAsync()).IsOk.Should().BeTrue();
        attentes.Should().Be(2);
    }

    [Fact]
    public async Task Un_point_jamais_apparu_echoue_apres_le_delai()
    {
        _restore.IsEnabled().Returns(true);
        var attentes = 0;
        var mgr = new SafetyManager(_restore, _consent, _elevated, _ => { attentes++; return Task.CompletedTask; });
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns((DateTime?)null);

        var r = await mgr.EnsureRestorePointAsync();

        r.Error!.Value.Code.Should().Be(ErrorCodes.RestoreNotVerified, "Windows a accepté : ce n'est pas un refus");
        attentes.Should().Be(2 * (SafetyManager.LecturesDuPoint - 1), "deux créations, chacune relue");
        await _elevated.Received(2).ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>());
    }

    // VSS échoue par intermittence sur ma machine (8193 après une autre opération de
    // clichés) et réussit à l'essai suivant (03:39 puis 03:41, 04:28 puis 04:30). Une création sans point relu est
    // retentée une fois.
    // L'échec VSS intermittent remonte en REFUS (Checkpoint-Computer sort 1) ; c'est lui
    // que la retentative vise.
    [Fact]
    public async Task Un_refus_de_Windows_est_retente_une_fois_et_le_second_essai_suffit()
    {
        _restore.IsEnabled().Returns(true);
        var mgr = new SafetyManager(_restore, _consent, _elevated, _ => Task.CompletedTask);
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "VSS")), Task.FromResult(Result.Ok()));
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns(null, (DateTime?)DateTime.UtcNow);

        (await mgr.EnsureRestorePointAsync()).IsOk.Should().BeTrue();
        await _elevated.Received(2).ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deux_refus_disent_la_raison_du_dernier()
    {
        _restore.IsEnabled().Returns(true);
        var mgr = new SafetyManager(_restore, _consent, _elevated, _ => Task.CompletedTask);
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "premier")), Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "second")));
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns((DateTime?)null);

        var r = await mgr.EnsureRestorePointAsync();

        r.Error!.Value.Code.Should().Be(ErrorCodes.RestoreFailed);
        r.Error!.Value.Message.Should().Be("second");
    }

    [Fact]
    public async Task Un_point_absent_apres_la_premiere_creation_est_retente_une_fois()
    {
        _restore.IsEnabled().Returns(true);
        var mgr = new SafetyManager(_restore, _consent, _elevated, _ => Task.CompletedTask);
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        var reponses = Enumerable.Repeat<DateTime?>(null, 1 + SafetyManager.LecturesDuPoint).Append(DateTime.UtcNow).ToArray();
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns(reponses[0], reponses[1..]);

        (await mgr.EnsureRestorePointAsync()).IsOk.Should().BeTrue();
        await _elevated.Received(2).ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_create_verification_failure_blocks()
    {
        // L'appel de création « réussit » mais le point ne s'est jamais matérialisé
        // (GPO ou édition l'ont neutralisé en silence). On ne doit pas rendre Ok.
        _restore.IsEnabled().Returns(true);
        var mgr = Build();
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns((DateTime?)null);

        var r = await mgr.EnsureRestorePointAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RestoreNotVerified, "accepté puis introuvable : son propre code depuis le 06/10");
    }

    [Fact]
    public async Task Throttles_to_one_point_per_session()
    {
        _restore.IsEnabled().Returns(true);
        var mgr = Build();

        var r1 = await mgr.EnsureRestorePointAsync();
        var r2 = await mgr.EnsureRestorePointAsync();

        r1.IsOk.Should().BeTrue();
        r2.IsOk.Should().BeTrue();
        // Un seul vrai point de restauration par session ou lot (pas de spam).
        await _elevated.Received(1).ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>());
        // Et la seconde demande ne relit même pas Windows : sans la garde de session, elle retrouverait le point récent
        // et rendrait Ok de toute façon, ce qui ne prouverait rien de la garde.
        _restore.Received(1).IsEnabled();
    }

    // --- B2 ---

    [Fact]
    public async Task Revert_restores_captured_original_registry_value()
    {
        var mgr = Build();
        // OriginalValueJson est la forme de fil de la PrivilegedOp typée (la forme codec),
        // portant la vraie valeur d'origine capturée à l'application.
        var original = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
            "ReflexEnabled", RegistryValueType.DWord, "0");
        var json = PrivilegedOpCodec.Serialize(original).Value!;
        var snap = new TweakSnapshot("latency.reflex", json);

        var r = await mgr.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Value == "0" && o.Name == "ReflexEnabled"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_restores_captured_original_power_plan()
    {
        var mgr = Build();
        var original = new SetActivePowerPlanOp(KnownPowerPlans.Balanced);
        var json = PrivilegedOpCodec.Serialize(original).Value!;
        var snap = new TweakSnapshot("fixit.powerplan", json);

        var r = await mgr.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetActivePowerPlanOp>(o => o.PlanGuid == KnownPowerPlans.Balanced),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_json()
    {
        var mgr = Build();

        var r = await mgr.RevertAsync(new TweakSnapshot("x", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_null_or_empty_snapshot_json()
    {
        var mgr = Build();

        (await mgr.RevertAsync(new TweakSnapshot("x", ""))).Error!.Value.Code
            .Should().Be(ErrorCodes.SnapshotInvalid);
        (await mgr.RevertAsync(new TweakSnapshot("x", null!))).Error!.Value.Code
            .Should().Be(ErrorCodes.SnapshotInvalid);
    }

    [Fact]
    public async Task Revert_surfaces_elevated_failure_without_throwing()
    {
        var mgr = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "write failed")));

        var original = new SetActivePowerPlanOp(KnownPowerPlans.Balanced);
        var snap = new TweakSnapshot("fixit.powerplan", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await mgr.RevertAsync(snap);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    // Le clic sur Appliquer attend ce point depuis le fil de l'interface : les lectures WMI (77 + 133 ms à froid sur la
    // machine de référence, plus avec beaucoup de points) ne s'y font pas, l'appel rend la main avant elles.
    [Fact]
    public async Task Les_lectures_de_la_restauration_ne_bloquent_pas_l_appelant()
    {
        using var lu = new ManualResetEventSlim();
        _restore.IsEnabled().Returns(_ => { lu.Wait(TimeSpan.FromSeconds(10)); return true; });
        var safety = Build();

        var appel = Task.Factory.StartNew(() => safety.EnsureRestorePointAsync(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

        (await Task.WhenAny(appel, Task.Delay(TimeSpan.FromSeconds(2)))).Should().BeSameAs(appel,
            "l'appelant reprend la main pendant la lecture WMI");
        lu.Set();
        (await await appel).IsOk.Should().BeTrue();
    }

    // « Optimiser » puis le réglage de la carte avant la fin du premier : les deux attendent le même point, un seul est créé.
    [Fact]
    public async Task Deux_demandes_en_meme_temps_ne_creent_qu_un_point()
    {
        using var lu = new ManualResetEventSlim();
        var crees = 0;
        _restore.IsEnabled().Returns(_ => { lu.Wait(TimeSpan.FromSeconds(10)); return true; });
        var safety = Build();
        _restore.DernierPointDepuis(Arg.Any<DateTime>()).Returns(_ => Volatile.Read(ref crees) > 0 ? DateTime.UtcNow : null);
        _elevated.ExecuteAsync(Arg.Any<CreateRestorePointOp>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Delay(100);
            Interlocked.Increment(ref crees);
            return Result.Ok();
        });

        var premiere = Task.Run(safety.EnsureRestorePointAsync);
        var seconde = Task.Run(safety.EnsureRestorePointAsync);
        await Task.Delay(200);
        lu.Set();

        (await premiere).IsOk.Should().BeTrue();
        (await seconde).IsOk.Should().BeTrue();
        crees.Should().Be(1);
    }

    // La question au joueur est une fenêtre : elle revient sur le fil de l'appelant (celui de l'interface), même si la
    // lecture de la restauration est partie sur le pool.
    [Fact]
    public async Task La_question_au_joueur_se_pose_sur_le_fil_de_l_appelant()
    {
        var filDeLaQuestion = -1;
        _restore.IsEnabled().Returns(false);
        _restore.TryEnable().Returns(true);
        _consent.AskEnableRestore().Returns(_ => { filDeLaQuestion = Environment.CurrentManagedThreadId; return true; });
        var safety = Build();
        using var fil = new FilUnique();
        var fini = new TaskCompletionSource<Result>();

        fil.Post(async _ => fini.SetResult(await safety.EnsureRestorePointAsync()), null);

        (await fini.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsOk.Should().BeTrue();
        filDeLaQuestion.Should().Be(fil.Id);
    }

    private sealed class FilUnique : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _file = new();
        private readonly Thread _fil;
        public int Id => _fil.ManagedThreadId;

        public FilUnique()
        {
            _fil = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (d, s) in _file.GetConsumingEnumerable()) d(s);
            }) { IsBackground = true };
            _fil.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _file.Add((d, state));
        public void Dispose() => _file.CompleteAdding();
    }
}
