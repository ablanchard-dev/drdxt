using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuUndervoltTweak.
//
// Un décalage négatif prudent de l'horloge cœur (+ limite de puissance)
// via NVAPI / ADLX, pas une courbe V/F complète. Séquence :
//   (1) filtre dur AppliesTo = dGPU de bureau seulement (faux sur portable, Optimus,
//       iGPU, console portable),
//   (2) bornage via GpuSafeBounds ; hors bornes → TWEAK.NOT_APPLICABLE,
//   (3) capture du décalage d'origine (couture de lecture) → op inverse sérialisée dans l'instantané,
//   (4) application via IElevatedClient (ApplyNvApiClockOffsetOp / ApplyAdlxTuningOp),
//   (5) test de stabilité (IStabilityTester) → si instable → retour arrière immédiat et
//       rendre TWEAK.UNSTABLE (jamais Ok tant que c'est instable).
//
// Honnêteté : SummaryKey = efficacité / plus frais / 1 % bas plus réguliers, jamais « +FPS ».
// Tests unitaires purs avec des faux NSubstitute : aucun accès réel au SDK ni à la carte.
// -----------------------------------------------------------------------
public sealed class GpuUndervoltTweakTests
{
    private readonly INvApiGpuTuning _nvApi = Substitute.For<INvApiGpuTuning>();
    private readonly IAdlxGpuTuning _adlx = Substitute.For<IAdlxGpuTuning>();
    private readonly IStabilityTester _stability = Substitute.For<IStabilityTester>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Hw(string gpuModel, string gpuVendor, long vram, ChassisType chassis) => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo(gpuModel, gpuVendor, vram, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        chassis);

    private static HardwareInventory DesktopRtx4070() => Hw("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, ChassisType.Desktop);
    private static HardwareInventory DesktopAmd() => Hw("AMD Radeon RX 7800 XT", "AMD", 16L << 30, ChassisType.Desktop);
    private static HardwareInventory LaptopOptimus() => Hw("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", 8L << 30, ChassisType.Laptop);
    private static HardwareInventory HandheldDeck() => Hw("AMD Custom GPU 0405", "AMD", 0, ChassisType.Handheld);

    private static HardwareInventory IgpuOnly() => new(
        new CpuInfo("Core i5-13600", "Intel", 14, 20, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("Intel UHD Graphics 770", "Intel", 0, "31.0", TriState.Unknown) },
        new[] { new MemoryModule("PN", "Corsair", 5600, 5600) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    // Un décalage d'undervolt prudent, dans les bornes (GpuSafeBounds).
    private static GpuTuningTarget SafeTarget(int coreOffsetMhz = -120) =>
        new(GpuIndex: 0, CoreOffsetMhz: coreOffsetMhz, MemoryOffsetMhz: 0);

    private GpuUndervoltTweak Build(GpuTuningTarget? target = null, HardwareInventory? amdPresenceHw = null)
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0));      // origine = décalage 0
        _nvApi.GetMemoryClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _adlx.IsAvailable.Returns(false);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(0)); // par défaut : une seule carte réglable, en 0
        _adlx.GetUndervoltValue(0).Returns(Result<int>.Ok(0));
        // ADLX lit et écrit une MaxFreq absolue ; l'application AMD lit l'horloge max absolue
        // courante pour construire la cible (courante + décalage) et capturer l'origine absolue.
        _adlx.GetGfxMaxFreqMhz(Arg.Any<int>()).Returns(Result<int>.Ok(2900)); // horloge max d'origine
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Stable)));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // Le levier lit sa cible dans l'IGpuTuningTargetSource vivant ; on y pose
        // la cible de réglage du test (la limite de puissance par défaut n'est pas utilisée ici).
        var source = new GpuTuningTargetSource(target ?? SafeTarget(), new GpuPowerLimitTarget(0, 90));
        // La présence d'un GPU AMD est un vrai fait matériel calculé par la source depuis
        // l'inventaire scanné (jamais forcé sur une fausse couture) : posée depuis un vrai matériel AMD à la demande.
        if (amdPresenceHw is not null)
            source.SetAmdGpuPresenceFromHardware(amdPresenceHw);
        return new GpuUndervoltTweak(_nvApi, _adlx, _stability, _elevated, source);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("gpu.undervolt");
        t.Category.Should().Be(TweakCategory.GpuTuning);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_no_fps_claim()
    {
        // Un undervolt sur un bureau déjà frais vaut ~0 sur les FPS de pointe.
        Build().SummaryKey.Should().Be("tweak.gpu.undervolt.efficiency");
        Build().SummaryKey.Should().NotContain("fps");
    }

    // --- Filtre dur : dGPU de bureau seulement. ---

    [Fact]
    public void AppliesTo_true_on_desktop_nvidia_dgpu() =>
        Build().AppliesTo(DesktopRtx4070(), State).Should().BeTrue();

    [Fact]
    public void AppliesTo_true_on_desktop_amd_dgpu()
    {
        var t = Build();
        _adlx.IsAvailable.Returns(true);
        _nvApi.IsAvailable.Returns(false);
        t.AppliesTo(DesktopAmd(), State).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_false_on_laptop_optimus() =>
        Build().AppliesTo(LaptopOptimus(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_igpu_only() =>
        Build().AppliesTo(IgpuOnly(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_handheld() =>
        Build().AppliesTo(HandheldDeck(), State).Should().BeFalse();

    // --- Bornage : décalage hors bornes → TWEAK.NOT_APPLICABLE, rien d'écrit. ---

    [Fact]
    public async Task Apply_rejects_offset_outside_safe_bounds()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -500)); // bien au-delà de MinCoreOffsetMhz

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakNotApplicable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_rejects_memory_offset_outside_safe_bounds()
    {
        var t = Build(new GpuTuningTarget(0, CoreOffsetMhz: -100, MemoryOffsetMhz: 5000));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakNotApplicable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // La mémoire ne s'écrit pas : la cible d'un profil (+200) ne part pas dans l'op.
    [Fact]
    public async Task L_op_ne_porte_aucune_memoire()
    {
        var t = Build(new GpuTuningTarget(GpuIndex: 0, CoreOffsetMhz: -120, MemoryOffsetMhz: 200));

        (await t.ApplyAsync()).IsOk.Should().BeTrue();

        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == -120 && o.MemoryOffsetMhz == 0), Arg.Any<CancellationToken>());
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.MemoryOffsetMhz != 0), Arg.Any<CancellationToken>());
    }

    // DrDXT n'empile ni n'écrase le réglage d'un autre outil : un
    // décalage cœur d'un autre outil (+15) ou une mémoire déjà montée (+1000) font refuser, et rien n'atteint le helper.
    [Theory]
    [InlineData(15, 0)]
    [InlineData(0, 1000)]
    public async Task Un_decalage_pose_par_un_autre_outil_fait_refuser_sans_rien_ecrire(int coeur, int memoire)
    {
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(coeur));
        _nvApi.GetMemoryClockOffsetMhz(0).Returns(Result<int>.Ok(memoire));

        var r = await t.ApplyAsync();

        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuReglageTiers);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<ApplyNvApiClockOffsetOp>(), Arg.Any<CancellationToken>());
        await _stability.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Seul_le_decalage_de_drdxt_lui_meme_passe()
    {
        GpuUndervoltTweak.ReglageTiers(0, 0, -120).Should().BeFalse();
        GpuUndervoltTweak.ReglageTiers(-120, 0, -120).Should().BeFalse("le décalage que DrDXT pose lui-même");
        GpuUndervoltTweak.ReglageTiers(-90, 0, -120).Should().BeTrue();
        GpuUndervoltTweak.ReglageTiers(0, 50, -120).Should().BeTrue("DrDXT n'écrit jamais la mémoire");
    }

    // --- Application : capture de l'origine → instantané, application via le helper (décalage d'horloge NVAPI). ---

    [Fact]
    public async Task Apply_captures_original_offset_and_applies_via_helper()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(-120)); // déjà posé par DrDXT lui-même

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Le décalage demandé a été appliqué via l'op typée en liste blanche.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.GpuIndex == 0 && o.CoreOffsetMhz == -120),
            Arg.Any<CancellationToken>());
        // L'instantané porte le décalage d'origine capturé (-120) en op inverse.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<ApplyNvApiClockOffsetOp>()
            .Which.CoreOffsetMhz.Should().Be(-120);
    }

    [Fact]
    public async Task Apply_runs_stability_test_before_returning_ok()
    {
        var t = Build();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _stability.Received(1).RunAsync(0, Arg.Any<CancellationToken>());
    }

    // --- Le test de stabilité échoue → retour immédiat, rendre TWEAK.UNSTABLE. ---

    [Fact]
    public async Task Apply_reverts_immediately_and_fails_unstable_when_stability_test_crashes()
    {
        var t = Build();
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable)));

        var r = await t.ApplyAsync();

        // Jamais Ok tant que c'est instable.
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        // Trois appels au helper : (1) l'écriture identité de l'origine capturée par la sonde de
        // capacité, (2) le décalage d'undervolt, (3) le retour arrière qui rejoue l'origine.
        await _elevated.Received(3).ExecuteAsync(Arg.Any<ApplyNvApiClockOffsetOp>(), Arg.Any<CancellationToken>());
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 0), Arg.Any<CancellationToken>());
    }

    // Le retour d'un décalage instable se fait pendant la pose, donc dans sa portée : marqué, le helper le rejouerait
    // après un arrêt sec et remettrait le décalage instable.
    [Fact]
    public async Task Le_retour_d_un_decalage_instable_part_hors_de_la_portee_de_pose()
    {
        var t = Build();
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable)));
        var vues = new List<PorteeDePose?>();
        _elevated.When(e => e.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()))
            .Do(_ => vues.Add(PorteeDePose.Courante));
        var portee = PorteeDePose.Ouvrir();

        await t.ApplyAsync();

        vues.Should().HaveCount(3);
        vues[1].Should().BeSameAs(portee, "le décalage lui-même est posé dans la portée");
        vues[2].Should().BeNull();
    }

    [Fact]
    public async Task Apply_reports_failed_revert_distinctly_when_unstable_and_revert_also_fails()
    {
        // Sécurité et honnêteté : si la carte est instable et que le retour arrière automatique
        // échoue lui-même, on ne doit pas dire « annulé immédiatement » ; le message doit
        // refléter honnêtement l'échec du retour (carte toujours sous-voltée et instable).
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0)); // décalage d'origine = 0
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable)));
        // L'undervolt appliqué (décalage -120) réussit ; le retour (décalage à 0) échoue.
        _elevated.ExecuteAsync(
                Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == -120), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // L'op « réécrire l'origine (0) » sert deux fois : à la sonde de capacité (écriture
        // identité, qui doit réussir sinon on ne règle rien) et au retour arrière. Même op par
        // construction → séquencer le faux : 1er appel ok (sonde), 2e appel échoue (retour).
        _elevated.ExecuteAsync(
                Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 0), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()),
                     Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "revert write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        // Un code a part, pour que le journal reste et que l'ecran ne dise pas « rendue ».
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
        // Le message doit refléter le retour raté, jamais la formule « annulé immédiatement ».
        r.Error!.Value.Message.Should().NotContain("reverted immediately");
        r.Error!.Value.Message.Should().Contain("revert failed");
        r.Error!.Value.Message.Should().Contain("System Restore");
    }

    [Fact]
    public async Task Apply_treats_inconclusive_stability_as_unsafe_and_reverts()
    {
        // Si le banc de stabilité ne peut pas tourner, on ne garde pas un undervolt non vérifié.
        var t = Build();
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive)));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        // écriture identité de la sonde + application + retour = 3.
        await _elevated.Received(3).ExecuteAsync(Arg.Any<ApplyNvApiClockOffsetOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_treats_failed_stability_run_as_unsafe_and_reverts()
    {
        var t = Build();
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Fail(ErrorCodes.TweakSdkUnavailable, "no harness")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        // écriture identité de la sonde + application + retour = 3.
        await _elevated.Received(3).ExecuteAsync(Arg.Any<ApplyNvApiClockOffsetOp>(), Arg.Any<CancellationToken>());
    }

    // --- Branche AMD (undervolt ADLX). ---

    [Fact]
    public async Task Apply_uses_adlx_undervolt_on_amd_with_an_absolute_target_and_absolute_inverse()
    {
        // ADLX est absolu : l'op d'application porte MaxFreq courante + décalage (2900 + (-100) = 2800)
        // et l'op inverse porte l'origine absolue (2900), pas une tension. C'est la symétrie d'unité
        // corrigée avec le chemin de limite de puissance ; le retour rend l'horloge d'origine exacte.
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.GetGfxMaxFreqMhz(0).Returns(Result<int>.Ok(2900)); // horloge max absolue courante

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.Kind == AdlxTuningKind.Undervolt && o.IntValue == 2800),
            Arg.Any<CancellationToken>());
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>()
            .Which.IntValue.Should().Be(2900, "the inverse restores the absolute ORIGINAL MaxFreq");
    }

    [Fact]
    public async Task Apply_amd_fails_clean_when_current_maxfreq_read_fails()
    {
        // Pas de MaxFreq absolue courante → impossible de construire une cible et un inverse absolus
        // fidèles, donc échec propre et aucune application (jamais deviner une horloge courante).
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.GetGfxMaxFreqMhz(Arg.Any<int>())
            .Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no gfx read"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- Choix de la carte AMD dédiée : résoudre la dGPU réglable, pas l'iGPU. ---

    [Fact]
    public async Task Apply_amd_resolves_discrete_gpu_index_and_targets_it_throughout()
    {
        // iGPU à l'index 0 (non réglable) + dGPU AMD dédiée à l'index 1. Le résolveur choisit 1,
        // et la capture, l'application et le test de stabilité visent tous l'index 1, jamais l'iGPU en 0.
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(1));
        _adlx.GetGfxMaxFreqMhz(1).Returns(Result<int>.Ok(2900)); // horloge max absolue courante sur la dGPU

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // L'op d'undervolt appliquée visait la carte dédiée à l'index 1 (absolu 2900 + (-100)).
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o =>
                o.GpuIndex == 1 && o.Kind == AdlxTuningKind.Undervolt && o.IntValue == 2800),
            Arg.Any<CancellationToken>());
        // Le test de stabilité a tourné sur le même index résolu (1).
        await _stability.Received().RunAsync(1, Arg.Any<CancellationToken>());
        await _stability.DidNotReceive().RunAsync(0, Arg.Any<CancellationToken>());
        // L'op inverse d'origine capturée (pour le retour arrière) porte l'index 1 et l'origine absolue.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        var op = inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>().Subject;
        op.GpuIndex.Should().Be(1);
        op.IntValue.Should().Be(2900);
    }

    [Fact]
    public async Task Apply_amd_reverts_on_resolved_index_when_unstable()
    {
        // Instable → le retour immédiat doit rejouer l'op inverse sur la même carte physique
        // résolue (index 1), en rendant la MaxFreq absolue d'origine (2900), pas une tension.
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(1));
        _adlx.GetGfxMaxFreqMhz(1).Returns(Result<int>.Ok(2900)); // horloge max absolue courante ; application → 2800
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable)));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        // Le retour a rejoué l'op inverse (retour à l'origine absolue 2900) sur l'index 1.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.GpuIndex == 1 && o.IntValue == 2900),
            Arg.Any<CancellationToken>());
        await _stability.Received().RunAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_amd_fails_clean_when_no_tunable_gpu_resolved()
    {
        // Machine sans GPU réglable (iGPU seule côté ADLX, par exemple) : le résolveur échoue →
        // échec propre, aucune op d'application exécutée, et jamais Ok.
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex()
            .Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no tunable AMD GPU found"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- Échec propre. ---

    [Fact]
    public async Task Apply_fails_clean_when_no_sdk_available()
    {
        var t = Build();
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // Un décalage non lu (cœur ou mémoire) n'est jamais 0 : sans savoir si un autre outil règle la carte, DrDXT refuse, et rien
    // n'atteint le helper.
    [Fact]
    public async Task Apply_fails_clean_when_original_capture_fails()
    {
        var t = Build();
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no nvapi"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuDecalageNonLu);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Une_memoire_non_lue_fait_refuser_au_lieu_de_compter_zero()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _nvApi.GetMemoryClockOffsetMhz(0).Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no memory read"));

        var r = await t.ApplyAsync();

        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuDecalageNonLu);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
        await _stability.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _nvApi.GetCoreClockOffsetMhz(0).Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // --- Honnêteté : GPU AMD présent mais ADLX indisponible → statut honnête explicite. ---

    [Fact]
    public async Task Apply_returns_amd_tuning_unavailable_when_amd_gpu_present_and_adlx_unavailable()
    {
        // Quand le réglage est impossible sur cette configuration, le dire, jamais sauter en silence.
        // Scénario : dGPU AMD de bureau détectée, NVAPI absent (attendu sur AMD), SDK ADLX pas
        // encore câblé dans cette version. Il faut un code honnête distinct, ni saut silencieux ni
        // succès. La présence vient de la vraie source qui la calcule depuis un vrai matériel AMD
        // (GpuGating.IsAmd), jamais forcée sur une fausse couture.
        var t = Build(amdPresenceHw: DesktopAmd());
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        // Non appliqué : ce serait malhonnête.
        r.IsOk.Should().BeFalse();
        // Code honnête explicite et distinct.
        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuAmdTuningUnavailable);
        // Distinct de l'erreur SDK générique (qui pourrait vouloir dire n'importe quoi).
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakSdkUnavailable,
            because: "must be distinct from a generic SDK unavailable");
        // Distinct du filtre matériel (les machines non AMD restent muettes ; celui-ci s'applique bien).
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakNotApplicable,
            because: "must not look like the tweak does not apply to this hardware");
        // Aucune écriture privilégiée tentée.
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- Sonde de capacité (doctrine de couverture matérielle) : un pilote qui dit « ok » et n'écrit rien. ---
    // C'est tout l'enjeu de la sonde : on ne possède pas le matériel de 2015 à 2026, donc le
    // support se prouve carte par carte. Une carte verrouillée, bridée par l'OEM ou quasi mobile
    // dont le pilote annonce un succès sans rien changer doit être refusée, pas créditée d'un faux undervolt.

    [Fact]
    public async Task Apply_refuses_when_the_identity_probe_write_does_not_take_nvidia()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        // 1re lecture = capture (0). 2e lecture = relecture de la sonde, et la carte n'a pas
        // gardé l'écriture identité (15 relu) → le chemin d'écriture n'est pas vivant ici.
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0), Result<int>.Ok(15));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnsupportedOnThisGpu);
        // Rien n'a été réglé : le décalage demandé n'a jamais atteint le helper.
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == -120), Arg.Any<CancellationToken>());
        // …et aucun test de stabilité n'a tourné sur une carte jamais touchée.
        await _stability.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_refuses_when_the_identity_probe_write_does_not_take_amd()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        // 1re lecture = capture (2900). 2e = relecture de la sonde : la carte a ignoré l'écriture.
        _adlx.GetGfxMaxFreqMhz(0).Returns(Result<int>.Ok(2900), Result<int>.Ok(2500));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnsupportedOnThisGpu);
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.IntValue == 2800), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_probe_writes_the_captured_original_back_which_changes_nothing()
    {
        // La sonde doit être sans effet pour le matériel : elle écrit la valeur qu'elle vient de lire.
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0)); // constante → la sonde passe

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // La sonde a écrit 0 (l'origine capturée), soit la valeur courante sur elle-même.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 0), Arg.Any<CancellationToken>());
    }

    // --- Le retour arrière rend l'origine capturée. ---

    [Fact]
    public async Task Revert_restores_the_captured_original_via_helper()
    {
        var t = Build();
        var original = new ApplyNvApiClockOffsetOp(0, CoreOffsetMhz: 25, MemoryOffsetMhz: 0);
        var snap = new TweakSnapshot("gpu.undervolt", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("gpu.undervolt", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
