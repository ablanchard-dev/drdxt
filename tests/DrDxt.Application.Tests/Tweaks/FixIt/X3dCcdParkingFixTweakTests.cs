using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// -----------------------------------------------------------------------
// X3dCcdParkingFixTweak (la fonction phare, voie supportée seulement).
//
// AppliesTo = AMD X3D à deux CCD (IsX3D && CcdCount >= 2) et parcage qui semble faux
// (heuristique sur SettingsState.ParkedCoreIndexes). Faux sur X3D à un CCD
// (7800X3D / 9800X3D), Intel, et hors X3D.
//
// Voie supportée seulement : activer le service AMD 3D V-Cache Performance
// Optimizer (RunServiceControlOp("AMD3DVCacheSvc", Start)) + Game Bar. Aucun bidouillage
// noyau, aucune écriture BIOS. Capture l'état d'origine du service et de la Game Bar →
// instantané inverse composite ; le retour arrière les restaure. CppcEnable n'est plus écrit ; un
// ancien instantané qui le porte s'annule toujours.
//
// Tests unitaires purs avec des faux NSubstitute.
// -----------------------------------------------------------------------
public sealed class X3dCcdParkingFixTweakTests
{
    private const string CppcPath = @"SYSTEM\CurrentControlSet\Control\Processor";
    private const string CppcName = "CppcEnable";
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string GameBarName = "AllowAutoGameMode";
    private const string Service = "AMD3DVCacheSvc";

    private readonly IServiceStateReader _service = Substitute.For<IServiceStateReader>();
    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    // X3D à deux CCD (7950X3D = 16 cœurs / 2 CCD) sans rien de parqué = l'état cassé
    // (l'optimiseur n'envoie pas les jeux sur le CCD V-Cache).
    private static SettingsState DualCcdBadParking() => StateWith(Array.Empty<int>());

    // Dual-CCD X3D dont le parking n'a pas pu etre lu. Distinct de "rien n'est parque" :
    // le lecteur etait un bouchon qui rendait toujours une liste vide, donc l'etat casse
    // ci-dessus etait declare sur toute machine X3D bi-CCD, et ce tweak ecrit
    // (AMD3DVCacheSvc + CppcEnable). Ne jamais proposer une ecriture sur un signal non lu.
    private static SettingsState DualCcdUnreadableParking() => StateWith(null);

    // X3D à deux CCD avec le CCD fréquence (index hauts 8..15) parqué = sain :
    // les jeux tournent sur le CCD0 V-Cache (0..7). Rien à corriger.
    private static SettingsState DualCcdGoodParking() => StateWith(new[] { 8, 9, 10, 11, 12, 13, 14, 15 });

    private static SettingsState StateWith(IReadOnlyList<int>? parked) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: parked, Vrr: TriState.On);

    private static HardwareInventory Cpu(string model, string vendor, bool isX3D, int ccdCount, int cores) => new(
        new CpuInfo(model, vendor, cores, cores * 2, isX3D, ccdCount),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4090", "NVIDIA", 24L << 30, "551.86", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "X670E"),
        new BiosInfo("1.2", null, true),
        ChassisType.Desktop);

    private static HardwareInventory Ryzen7950X3D() => Cpu("AMD Ryzen 9 7950X3D", "AMD", isX3D: true, ccdCount: 2, cores: 16);
    private static HardwareInventory Ryzen7800X3D_singleCcd() => Cpu("AMD Ryzen 7 7800X3D", "AMD", isX3D: true, ccdCount: 1, cores: 8);
    private static HardwareInventory Ryzen9800X3D_singleCcd() => Cpu("AMD Ryzen 7 9800X3D", "AMD", isX3D: true, ccdCount: 1, cores: 8);
    private static HardwareInventory IntelCpu() => Cpu("Intel Core i9-14900K", "Intel", isX3D: false, ccdCount: 1, cores: 24);
    private static HardwareInventory NonX3dAmd() => Cpu("AMD Ryzen 9 7950X", "AMD", isX3D: false, ccdCount: 2, cores: 16);

    private X3dCcdParkingFixTweak Build()
    {
        _service.GetServiceState(Service).Returns(Result<ServiceState>.Ok(ServiceState.Stopped));
        _registry.ReadDword(RegistryHiveKind.LocalMachine, CppcPath, CppcName).Returns(Result<uint?>.Ok(0u));
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName).Returns(Result<uint?>.Ok(0u));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new X3dCcdParkingFixTweak(_service, _registry, _elevated);
    }

    [Fact]
    public void Unreadable_parking_never_makes_the_write_apply()
    {
        // "Je n'ai pas pu lire" n'est pas "rien n'est parque". Confondre les deux fait
        // proposer une ecriture sur une machine saine.
        var sut = new X3dCcdParkingFixTweak(_service, _registry, _elevated);

        sut.AppliesTo(Ryzen7950X3D(), DualCcdUnreadableParking()).Should().BeFalse();
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("fixit.x3d_ccd");
        t.Category.Should().Be(TweakCategory.FixIt);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_about_repairing_ccd_routing_never_fps()
    {
        var t = Build();
        t.SummaryKey.Should().Be("tweak.fixit.x3d_ccd.repair_routing");
        t.SummaryKey.Should().NotContain("fps");
    }

    [Fact]
    public void AppliesTo_true_only_on_dual_ccd_x3d_with_bad_parking() =>
        Build().AppliesTo(Ryzen7950X3D(), DualCcdBadParking()).Should().BeTrue();

    [Fact]
    public void AppliesTo_false_on_dual_ccd_x3d_with_healthy_parking() =>
        Build().AppliesTo(Ryzen7950X3D(), DualCcdGoodParking()).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_single_ccd_x3d_7800x3d() =>
        Build().AppliesTo(Ryzen7800X3D_singleCcd(), DualCcdBadParking()).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_single_ccd_x3d_9800x3d() =>
        Build().AppliesTo(Ryzen9800X3D_singleCcd(), DualCcdBadParking()).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_intel() =>
        Build().AppliesTo(IntelCpu(), DualCcdBadParking()).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_non_x3d_dual_ccd() =>
        Build().AppliesTo(NonX3dAmd(), DualCcdBadParking()).Should().BeFalse();

    [Fact]
    public async Task Apply_uses_supported_route_only_service_and_gamebar_never_cppc()
    {
        var t = Build();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // (1) Démarrer le service AMD 3D V-Cache Performance Optimizer.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<RunServiceControlOp>(o => o.ServiceName == Service && o.Action == ServiceAction.Start),
            Arg.Any<CancellationToken>());
        // Plus jamais CppcEnable, aucune source ne montre que Windows le lise.
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Path == CppcPath && o.Name == CppcName),
            Arg.Any<CancellationToken>());
        // (2) Mode jeu automatique de la Game Bar = 1.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Path == GameBarPath && o.Name == GameBarName && o.Value == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_NEVER_writes_a_bios_or_kernel_op()
    {
        var t = Build();

        await t.ApplyAsync();

        // Les seuls types d'op que le correctif X3D peut envoyer sont la voie logicielle supportée.
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<ApplyNvApiClockOffsetOp>(), Arg.Any<CancellationToken>());
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<ApplyNvApiPowerLimitOp>(), Arg.Any<CancellationToken>());
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<ApplyGpuFanCurveOp>(), Arg.Any<CancellationToken>());
        // Chaque op envoyée a l'une des trois formes de la voie supportée.
        var calls = _elevated.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IElevatedClient.ExecuteAsync))
            .Select(c => c.GetArguments()[0])
            .ToList();
        calls.Should().NotBeEmpty();
        calls.All(op =>
            op is RunServiceControlOp or SetRegistryValueOp or DeleteRegistryValueOp).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_captures_prior_service_and_gamebar_in_snapshot()
    {
        var t = Build();
        _service.GetServiceState(Service).Returns(Result<ServiceState>.Ok(ServiceState.Stopped));
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName).Returns(Result<uint?>.Ok((uint?)null));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // L'instantané est un composite des ops inverses d'origine capturées.
        var decoded = CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson);
        decoded.IsOk.Should().BeTrue();
        var ops = decoded.Value!;
        // Le service était arrêté à l'origine → inverse = l'arrêter de nouveau.
        ops.OfType<RunServiceControlOp>()
            .Any(s => s.ServiceName == Service && s.Action == ServiceAction.Stop).Should().BeTrue();
        // Plus rien sur CppcEnable : ce n'est plus écrit, donc rien à rendre.
        ops.OfType<SetRegistryValueOp>().Any(o => o.Name == CppcName).Should().BeFalse();
        // La valeur Game Bar était absente à l'origine → inverse = la supprimer.
        ops.OfType<DeleteRegistryValueOp>()
            .Any(d => d.Path == GameBarPath && d.Name == GameBarName).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_does_not_restart_an_already_running_service_inverse_is_noop_safe()
    {
        var t = Build();
        _service.GetServiceState(Service).Returns(Result<ServiceState>.Ok(ServiceState.Running));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Le service tournait déjà → l'inverse capturé ne doit pas l'arrêter au retour arrière.
        var decoded = CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson);
        decoded.IsOk.Should().BeTrue();
        decoded.Value!.OfType<RunServiceControlOp>()
            .Any(s => s.Action == ServiceAction.Stop).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_fails_clean_when_service_state_unreadable()
    {
        var t = Build();
        _service.GetServiceState(Service).Returns(Result<ServiceState>.Fail(ErrorCodes.TweakSdkUnavailable, "no scm"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_fails_clean_when_gamebar_read_fails()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName)
            .Returns(Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "reg unreadable"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Un instantané d'une ancienne version porte encore l'inverse de CppcEnable : il doit
    /// toujours s'annuler en entier, sinon une ancienne écriture resterait sur la machine.</summary>
    [Fact]
    public async Task Un_ancien_instantane_avec_CppcEnable_s_annule_toujours()
    {
        var t = Build();
        var ancien = CompositeSnapshotCodec.Encode(new List<PrivilegedOp>
        {
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, CppcPath, CppcName, RegistryValueType.DWord, "0"),
            new RunServiceControlOp(Service, ServiceAction.Stop),
        }).Value!;

        var revert = await t.RevertAsync(new TweakSnapshot("fixit.x3d_ccd", ancien));

        revert.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Path == CppcPath && o.Name == CppcName && o.Value == "0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "service start failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _service.GetServiceState(Service).Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_rolls_back_already_applied_ops_when_a_mid_apply_op_fails()
    {
        // « rien ne reste pendu » : l'op (1) Start du service réussit, l'op (2) Game Bar
        // échoue → le levier doit rejouer l'inverse de l'op (1) (le service était arrêté
        // à l'origine → l'arrêter) et rendre Fail, sans laisser d'état appliqué orphelin.
        var t = Build();
        _service.GetServiceState(Service).Returns(Result<ServiceState>.Ok(ServiceState.Stopped));

        var startOk = Task.FromResult(Result.Ok());
        var gameBarFail = Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "game bar write failed"));
        _elevated.ExecuteAsync(
                Arg.Is<RunServiceControlOp>(o => o.Action == ServiceAction.Start), Arg.Any<CancellationToken>())
            .Returns(startOk);
        _elevated.ExecuteAsync(
                Arg.Is<SetRegistryValueOp>(o => o.Path == GameBarPath && o.Name == GameBarName), Arg.Any<CancellationToken>())
            .Returns(gameBarFail);
        // Le rollback inverse (Stop du service) a le droit de réussir.
        _elevated.ExecuteAsync(
                Arg.Is<RunServiceControlOp>(o => o.Action == ServiceAction.Stop), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));

        var r = await t.ApplyAsync();

        // La méthode rend Fail (le code d'échec d'origine), pas Ok.
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        // L'inverse de l'op (1) déjà appliquée a bien été rejoué : le service a été arrêté.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<RunServiceControlOp>(o => o.ServiceName == Service && o.Action == ServiceAction.Stop),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_restores_prior_service_and_gamebar_state()
    {
        var t = Build();
        var r = await t.ApplyAsync();
        r.IsOk.Should().BeTrue();
        _elevated.ClearReceivedCalls();

        var revert = await t.RevertAsync(r.Value!);

        revert.IsOk.Should().BeTrue();
        // Le service était arrêté à l'origine → le retour arrière l'arrête.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<RunServiceControlOp>(o => o.ServiceName == Service && o.Action == ServiceAction.Stop),
            Arg.Any<CancellationToken>());
        // Game Bar rendue à l'origine 0.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Path == GameBarPath && o.Name == GameBarName && o.Value == "0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("fixit.x3d_ccd", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
