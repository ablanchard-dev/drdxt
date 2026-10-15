using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuPowerLimitTweak.
//
// Mêmes invariants que l'undervolt : filtre dGPU de bureau, bornage via GpuSafeBounds (la
// limite de puissance ne descend jamais sous le plancher sûr : une carte affamée saccade, elle ne gagne pas),
// capture de l'origine → instantané en op inverse sérialisée, application via
// ApplyNvApiPowerLimitOp / ADLX en liste blanche, retour arrière, échec propre. Honnêteté :
// efficacité, plus frais, jamais « +FPS ».
// -----------------------------------------------------------------------
public sealed class GpuPowerLimitTweakTests
{
    private readonly INvApiGpuTuning _nvApi = Substitute.For<INvApiGpuTuning>();
    private readonly IAdlxGpuTuning _adlx = Substitute.For<IAdlxGpuTuning>();
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

    private GpuPowerLimitTweak Build(int requestedPct = 80, HardwareInventory? amdPresenceHw = null)
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(100)); // stock = 100%
        _adlx.IsAvailable.Returns(false);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(0)); // par défaut : une seule carte réglable, en 0
        _adlx.GetPowerLimitPct(0).Returns(Result<int>.Ok(100));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // Le levier lit sa cible dans l'IGpuTuningTargetSource vivant ; on y pose
        // la cible de limite de puissance du test (la cible de réglage par défaut n'est pas utilisée ici).
        var source = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, requestedPct));
        // La présence est un vrai fait matériel calculé par la source depuis l'inventaire scanné
        // (jamais forcé sur une fausse couture) : posée depuis un vrai matériel AMD à la demande.
        if (amdPresenceHw is not null)
            source.SetAmdGpuPresenceFromHardware(amdPresenceHw);
        return new GpuPowerLimitTweak(_nvApi, _adlx, _elevated, source);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("gpu.power_limit");
        t.Category.Should().Be(TweakCategory.GpuTuning);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_no_fps_claim()
    {
        Build().SummaryKey.Should().Be("tweak.gpu.power_limit.efficiency");
        Build().SummaryKey.Should().NotContain("fps");
    }

    // --- Filtre : dGPU de bureau seulement. ---

    [Fact]
    public void AppliesTo_true_on_desktop_dgpu() =>
        Build().AppliesTo(DesktopRtx4070(), State).Should().BeTrue();

    [Fact]
    public void AppliesTo_false_on_laptop() =>
        Build().AppliesTo(LaptopOptimus(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_handheld() =>
        Build().AppliesTo(HandheldDeck(), State).Should().BeFalse();

    // --- Bornage : jamais sous le plancher. ---

    [Fact]
    public async Task Apply_clamps_below_floor_request_up_to_the_floor()
    {
        var t = Build(requestedPct: 30); // sous MinPowerLimitPct

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // La valeur appliquée est relevée au plancher sûr, jamais les 30 % dangereux.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.PowerLimitPercent == GpuSafeBounds.MinPowerLimitPct),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_applies_an_in_range_request_unchanged()
    {
        var t = Build(requestedPct: 80);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.GpuIndex == 0 && o.PowerLimitPercent == 80),
            Arg.Any<CancellationToken>());
    }

    // --- Capture de l'origine → instantané. ---

    [Fact]
    public async Task Apply_captures_original_power_limit_in_snapshot()
    {
        var t = Build(requestedPct: 80);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(95)); // une limite antérieure quelconque

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<ApplyNvApiPowerLimitOp>()
            .Which.PowerLimitPercent.Should().Be(95);
    }

    // --- Branche AMD. ---

    [Fact]
    public async Task Apply_uses_adlx_power_limit_on_amd()
    {
        var t = Build(requestedPct: 80);
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.GetPowerLimitPct(0).Returns(Result<int>.Ok(100));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.Kind == AdlxTuningKind.PowerLimit && o.IntValue == 80),
            Arg.Any<CancellationToken>());
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>()
            .Which.IntValue.Should().Be(100);
    }

    // --- Choix de la carte AMD dédiée : résoudre la dGPU réglable, pas l'iGPU. ---

    [Fact]
    public async Task Apply_amd_resolves_discrete_gpu_index_and_targets_it()
    {
        // iGPU à l'index 0 (non réglable) + dGPU AMD dédiée à l'index 1. Le résolveur choisit 1,
        // et la capture comme l'application visent l'index 1, jamais l'iGPU en 0.
        var t = Build(requestedPct: 80);
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(1));
        _adlx.GetPowerLimitPct(1).Returns(Result<int>.Ok(95));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // L'op de limite de puissance appliquée visait la carte dédiée à l'index 1.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o =>
                o.GpuIndex == 1 && o.Kind == AdlxTuningKind.PowerLimit && o.IntValue == 80),
            Arg.Any<CancellationToken>());
        // L'op inverse d'origine capturée (pour le retour arrière) porte l'index 1 et la valeur lue.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        var op = inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>().Subject;
        op.GpuIndex.Should().Be(1);
        op.IntValue.Should().Be(95);
    }

    [Fact]
    public async Task Apply_amd_fails_clean_when_no_tunable_gpu_resolved()
    {
        // Machine sans GPU réglable : le résolveur échoue → échec propre, aucune op d'application, jamais Ok.
        var t = Build(requestedPct: 80);
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

    [Fact]
    public async Task Apply_fails_clean_when_capture_fails()
    {
        var t = Build();
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no nvapi"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "boom")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _nvApi.GetPowerLimitPct(0).Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // --- Honnêteté : GPU AMD présent mais ADLX indisponible → statut honnête explicite. ---

    [Fact]
    public async Task Apply_returns_amd_tuning_unavailable_when_amd_gpu_present_and_adlx_unavailable()
    {
        // Quand le réglage est impossible sur cette configuration, le dire, jamais sauter en silence.
        // La présence vient de la vraie source qui la calcule depuis un vrai matériel AMD
        // (GpuGating.IsAmd), jamais forcée sur une fausse couture.
        var t = Build(amdPresenceHw: DesktopAmd());
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuAmdTuningUnavailable);
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakSdkUnavailable,
            because: "must be distinct from a generic SDK unavailable");
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakNotApplicable,
            because: "must not look like the tweak does not apply to this hardware");
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- Sonde de capacité (doctrine de couverture matérielle). ---

    [Fact]
    public async Task Apply_refuses_when_the_identity_probe_write_does_not_take()
    {
        // Même doctrine que l'undervolt : prouver que l'écriture de limite de puissance est vivante
        // sur cette carte (écriture identité + relecture) avant de plafonner quoi que ce soit. Le pilote
        // dit ok, rien n'a bougé → refuser honnêtement, ne rien régler.
        var t = Build(requestedPct: 80);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(100), Result<int>.Ok(112));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnsupportedOnThisGpu);
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.PowerLimitPercent == 80), Arg.Any<CancellationToken>());
    }

    // --- Revert. ---

    [Fact]
    public async Task Revert_restores_the_captured_original_via_helper()
    {
        var t = Build();
        var original = new ApplyNvApiPowerLimitOp(0, PowerLimitPercent: 100);
        var snap = new TweakSnapshot("gpu.power_limit", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.PowerLimitPercent == 100), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("gpu.power_limit", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // La valeur inscrite pour la désinstallation est celle que la carte relit, pas celle demandée.
    [Fact]
    public async Task La_valeur_inscrite_est_celle_que_la_carte_relit()
    {
        var t = Build(requestedPct: 80);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(100), Result<int>.Ok(100), Result<int>.Ok(79));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(r.Value!.AppliedValueJson).Value.Should().Be(new ApplyNvApiPowerLimitOp(0, 79));
    }
}
