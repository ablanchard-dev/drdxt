using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuFanCurveTweak.
//
// Mêmes invariants que l'undervolt et la limite de puissance : filtre dGPU de bureau, bornage
// via GpuSafeBounds (la courbe ne descend jamais sous le régime minimal à haute température :
// anti-surchauffe), capture de la courbe d'origine → instantané en op inverse sérialisée,
// application via ApplyGpuFanCurveOp en liste blanche, retour arrière, échec propre. Honnêteté : plus frais, jamais « +FPS ».
// -----------------------------------------------------------------------
public sealed class GpuFanCurveTweakTests
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

    private static FanCurve StockCurve() => new(new[]
    {
        new FanCurvePoint(40, 30),
        new FanCurvePoint(85, 70),
    });

    // Une courbe demandée sûre, dans les bornes.
    private static FanCurve SafeRequest() => new(new[]
    {
        new FanCurvePoint(40, 35),
        new FanCurvePoint(85, 75),
    });

    // Une demande qui tourne en silence à chaud : GpuSafeBounds doit la relever.
    private static FanCurve HotSilentRequest() => new(new[]
    {
        new FanCurvePoint(40, 20),
        new FanCurvePoint(85, 10), // dangerous: 10% at 85°C
    });

    private GpuFanCurveTweak Build(FanCurve? request = null, HardwareInventory? amdPresenceHw = null)
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetFanCurve(0).Returns(Result<FanCurve>.Ok(StockCurve()));
        _adlx.IsAvailable.Returns(false);
        _adlx.GetFanCurve(0).Returns(Result<FanCurve>.Ok(StockCurve()));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // Le levier de courbe lit aussi l'IGpuTuningTargetSource vivant pour le fait matériel
        // « GPU AMD présent ». La présence est calculée par la source depuis le vrai matériel
        // (GpuGating.IsAmd), jamais forcée sur une fausse couture, et posée depuis un vrai matériel AMD à la demande.
        var source = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90));
        if (amdPresenceHw is not null)
            source.SetAmdGpuPresenceFromHardware(amdPresenceHw);
        return new GpuFanCurveTweak(_nvApi, _adlx, _elevated, source, new GpuFanCurveTarget(0, request ?? SafeRequest()));
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("gpu.fan_curve");
        t.Category.Should().Be(TweakCategory.GpuTuning);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_no_fps_claim()
    {
        Build().SummaryKey.Should().Be("tweak.gpu.fan_curve.cooler");
        Build().SummaryKey.Should().NotContain("fps");
    }

    // --- Filtre : dGPU de bureau seulement. ---

    // Le helper n'écrit aucune courbe (FanCurveDeferred). Même le cas le plus
    // favorable ne propose plus ce levier.
    [Fact]
    public void Jamais_propose_tant_que_le_helper_n_ecrit_pas_de_courbe() =>
        Build().AppliesTo(DesktopRtx4070(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_laptop() =>
        Build().AppliesTo(LaptopOptimus(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_handheld() =>
        Build().AppliesTo(HandheldDeck(), State).Should().BeFalse();

    // --- Bornage : une courbe silencieuse à chaud est relevée au plancher anti-surchauffe. ---

    [Fact]
    public async Task Apply_clamps_hot_silent_curve_to_the_overheat_floor()
    {
        var t = Build(HotSilentRequest());

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Le point chaud (85 °C) de la courbe appliquée est relevé au moins au régime sûr minimal.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyGpuFanCurveOp>(o =>
                o.GpuIndex == 0
                && o.Points.Any(p => p.TempC == 85 && p.DutyPct >= GpuSafeBounds.MinFanDutyAtHotTempPct)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_applies_a_safe_curve_unchanged()
    {
        var t = Build(SafeRequest());

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyGpuFanCurveOp>(o =>
                o.Points.Any(p => p.TempC == 85 && p.DutyPct == 75)),
            Arg.Any<CancellationToken>());
    }

    // --- Capture de l'origine → instantané. ---

    [Fact]
    public async Task Apply_captures_original_curve_in_snapshot()
    {
        var t = Build();
        _nvApi.GetFanCurve(0).Returns(Result<FanCurve>.Ok(new FanCurve(new[]
        {
            new FanCurvePoint(50, 45),
            new FanCurvePoint(85, 65),
        })));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        var op = inverse.Value.Should().BeOfType<ApplyGpuFanCurveOp>().Subject;
        op.Points.Should().Contain(p => p.TempC == 50 && p.DutyPct == 45);
        op.Points.Should().Contain(p => p.TempC == 85 && p.DutyPct == 65);
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
        _nvApi.GetFanCurve(0).Returns(Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable, "no nvapi"));

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
        _nvApi.GetFanCurve(0).Returns(_ => throw new InvalidOperationException("kaboom"));

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

    // --- Branche AMD. ---

    [Fact]
    public async Task Apply_uses_adlx_curve_read_on_amd()
    {
        var t = Build(SafeRequest());
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.GetFanCurve(0).Returns(Result<FanCurve>.Ok(StockCurve()));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(Arg.Any<ApplyGpuFanCurveOp>(), Arg.Any<CancellationToken>());
        _adlx.Received().GetFanCurve(0);
    }

    // --- Revert. ---

    [Fact]
    public async Task Revert_restores_the_captured_original_via_helper()
    {
        var t = Build();
        var original = new ApplyGpuFanCurveOp(0, new[] { new FanCurvePointDto(40, 30), new FanCurvePointDto(85, 70) });
        var snap = new TweakSnapshot("gpu.fan_curve", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyGpuFanCurveOp>(o => o.Points.Any(p => p.TempC == 85 && p.DutyPct == 70)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("gpu.fan_curve", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
