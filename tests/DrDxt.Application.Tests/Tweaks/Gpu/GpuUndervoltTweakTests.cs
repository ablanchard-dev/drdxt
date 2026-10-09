using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// M2 Phase E2 — GpuUndervoltTweak.
//
// Option A (master §11): a conservative NEGATIVE core-clock offset (+ power limit)
// via NVAPI / ADLX — not a full V/F curve. Sequence:
//   (1) AppliesTo HARD gating = desktop dGPU only (false on laptop / Optimus /
//       iGPU / handheld),
//   (2) clamp via GpuSafeBounds — out of bounds → TWEAK.NOT_APPLICABLE,
//   (3) capture the original offset (read seam) → serialized inverse op in snapshot,
//   (4) apply via IElevatedClient (ApplyNvApiClockOffsetOp / ApplyAdlxTuningOp),
//   (5) stability test (IStabilityTester) → if unstable → revert immediately and
//       return TWEAK.UNSTABLE (never Ok while unstable).
//
// HONESTY (§5): SummaryKey = efficiency / cooler / steadier 1% lows — never "+FPS".
// Pure unit tests with NSubstitute fakes — no real SDK / GPU access.
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

    // A conservative, in-bounds undervolt offset (within GpuSafeBounds).
    private static GpuTuningTarget SafeTarget(int coreOffsetMhz = -120) =>
        new(GpuIndex: 0, CoreOffsetMhz: coreOffsetMhz, MemoryOffsetMhz: 0);

    private GpuUndervoltTweak Build(GpuTuningTarget? target = null, HardwareInventory? amdPresenceHw = null)
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0));      // stock = 0 offset
        _nvApi.GetMemoryClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _adlx.IsAvailable.Returns(false);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(0)); // default: single tunable GPU at 0
        _adlx.GetUndervoltValue(0).Returns(Result<int>.Ok(0));
        // ADLX reads/writes an absolute MaxFreq; the AMD apply reads the current absolute max
        // clock to build the target (current + offset) and to capture the absolute original.
        _adlx.GetGfxMaxFreqMhz(Arg.Any<int>()).Returns(Result<int>.Ok(2900)); // stock max clock
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Stable)));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // M6 — the tweak now reads its target from the live IGpuTuningTargetSource; seed it
        // with the test's tuning target (the power-limit default is unused by this tweak).
        var source = new GpuTuningTargetSource(target ?? SafeTarget(), new GpuPowerLimitTarget(0, 90));
        // The AMD-GPU-presence fact is a real hardware fact computed by the source from the
        // scanned inventory (never forced on a fake seam) — set it from real AMD hw when asked.
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
        // §5: an undervolt on an already-cool desktop is ~0 on peak FPS.
        Build().SummaryKey.Should().Be("tweak.gpu.undervolt.efficiency");
        Build().SummaryKey.Should().NotContain("fps");
    }

    // --- HARD gating: desktop dGPU only. ---

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

    // --- Clamp: out-of-bounds offset → TWEAK.NOT_APPLICABLE, nothing written. ---

    [Fact]
    public async Task Apply_rejects_offset_outside_safe_bounds()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -500)); // far beyond MinCoreOffsetMhz

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

    // --- Apply: capture original → snapshot, apply via helper (NVAPI clock offset). ---

    [Fact]
    public async Task Apply_captures_original_offset_and_applies_via_helper()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(15)); // some prior offset

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Applied the requested offset via the allowlisted typed op.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.GpuIndex == 0 && o.CoreOffsetMhz == -120),
            Arg.Any<CancellationToken>());
        // Snapshot carries the captured original offset (15) as the inverse op.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<ApplyNvApiClockOffsetOp>()
            .Which.CoreOffsetMhz.Should().Be(15);
    }

    [Fact]
    public async Task Apply_runs_stability_test_before_returning_ok()
    {
        var t = Build();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _stability.Received(1).RunAsync(0, Arg.Any<CancellationToken>());
    }

    // --- Stability test fails → revert immediately, return TWEAK.UNSTABLE. ---

    [Fact]
    public async Task Apply_reverts_immediately_and_fails_unstable_when_stability_test_crashes()
    {
        var t = Build();
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable)));

        var r = await t.ApplyAsync();

        // Never Ok while unstable.
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        // Three helper calls now: (1) the capability probe's IDENTITY write of the captured
        // original (§6.0), (2) the undervolt offset, (3) the revert replaying the original.
        await _elevated.Received(3).ExecuteAsync(Arg.Any<ApplyNvApiClockOffsetOp>(), Arg.Any<CancellationToken>());
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_reports_failed_revert_distinctly_when_unstable_and_revert_also_fails()
    {
        // §11 safety/honesty: if the GPU is unstable AND the automatic revert itself
        // FAILS, we must not claim it "was reverted immediately" — the message must
        // honestly reflect that the revert failed (GPU still undervolted + unstable).
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0)); // original offset = 0
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable)));
        // The applied undervolt (offset -120) succeeds; the revert (offset back to 0) FAILS.
        _elevated.ExecuteAsync(
                Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == -120), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // The "write the original (0) back" op is used TWICE: once by the capability probe
        // (identity write, must succeed or we never tune at all) and once by the revert. Same
        // op by construction → sequence the fake: 1st call ok (probe), 2nd call FAILS (revert).
        _elevated.ExecuteAsync(
                Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 0), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()),
                     Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "revert write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        // 08/10/2026 (revue n° 13) : un code a part, pour que le journal reste et que l'ecran ne dise pas « rendue ».
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
        // The message must reflect the FAILED revert, never the "reverted immediately" wording.
        r.Error!.Value.Message.Should().NotContain("reverted immediately");
        r.Error!.Value.Message.Should().Contain("revert failed");
        r.Error!.Value.Message.Should().Contain("System Restore");
    }

    [Fact]
    public async Task Apply_treats_inconclusive_stability_as_unsafe_and_reverts()
    {
        // If the stability harness cannot run, we do not keep an unverified undervolt.
        var t = Build();
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Inconclusive)));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        // probe identity write + apply + revert = 3.
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
        // probe identity write + apply + revert = 3.
        await _elevated.Received(3).ExecuteAsync(Arg.Any<ApplyNvApiClockOffsetOp>(), Arg.Any<CancellationToken>());
    }

    // --- AMD branch (ADLX undervolt). ---

    [Fact]
    public async Task Apply_uses_adlx_undervolt_on_amd_with_an_absolute_target_and_absolute_inverse()
    {
        // ADLX is absolute: the apply op carries current MaxFreq + offset (2900 + (-100) = 2800)
        // and the INVERSE op carries the absolute original (2900) — not a voltage. This is the
        // fixed unit symmetry with the power-limit path; the revert restores the exact stock clock.
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.GetGfxMaxFreqMhz(0).Returns(Result<int>.Ok(2900)); // current absolute max clock

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
        // No absolute current MaxFreq → we cannot build a faithful absolute target/inverse, so
        // we fail clean and never apply (never guess a current clock).
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

    // --- AMD discrete-GPU selection (T4): resolve the tunable dGPU, not the iGPU. ---

    [Fact]
    public async Task Apply_amd_resolves_discrete_gpu_index_and_targets_it_throughout()
    {
        // iGPU at index 0 (not tunable) + discrete AMD dGPU at index 1. The resolver picks 1,
        // and the capture, apply AND stability test all target index 1 — never the iGPU at 0.
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(1));
        _adlx.GetGfxMaxFreqMhz(1).Returns(Result<int>.Ok(2900)); // current absolute max clock on the dGPU

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // The applied undervolt op targeted the discrete card at index 1 (absolute 2900 + (-100)).
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o =>
                o.GpuIndex == 1 && o.Kind == AdlxTuningKind.Undervolt && o.IntValue == 2800),
            Arg.Any<CancellationToken>());
        // The stability test ran on the same resolved index (1).
        await _stability.Received().RunAsync(1, Arg.Any<CancellationToken>());
        await _stability.DidNotReceive().RunAsync(0, Arg.Any<CancellationToken>());
        // The captured-original inverse op (for revert) carries index 1 and the absolute original.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        var op = inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>().Subject;
        op.GpuIndex.Should().Be(1);
        op.IntValue.Should().Be(2900);
    }

    [Fact]
    public async Task Apply_amd_reverts_on_resolved_index_when_unstable()
    {
        // Unstable → the immediate revert must replay the inverse op on the same resolved
        // physical GPU (index 1), restoring the absolute original MaxFreq (2900), not a voltage.
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(1));
        _adlx.GetGfxMaxFreqMhz(1).Returns(Result<int>.Ok(2900)); // current absolute max clock; apply → 2800
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Unstable)));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        // Revert replayed the inverse op (back to the absolute original 2900) on index 1.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.GpuIndex == 1 && o.IntValue == 2900),
            Arg.Any<CancellationToken>());
        await _stability.Received().RunAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_amd_fails_clean_when_no_tunable_gpu_resolved()
    {
        // 0-tunable-GPU box (e.g. iGPU only on the ADLX side): the resolver fails → fail-clean,
        // NO apply op is executed, and we never claim Ok.
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

    // --- Fail-clean. ---

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
    public async Task Apply_fails_clean_when_original_capture_fails()
    {
        var t = Build();
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no nvapi"));

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

    // --- Honesty: AMD GPU present but ADLX unavailable → explicit honest status. ---

    [Fact]
    public async Task Apply_returns_amd_tuning_unavailable_when_amd_gpu_present_and_adlx_unavailable()
    {
        // §22.3: when tuning cannot be done for this setup, SAY SO — never silently skip.
        // Scenario: AMD desktop dGPU detected, NVAPI absent (expected on AMD), ADLX SDK not
        // yet wired in this build. Must be a distinct honest code, not a silent skip and not
        // a success. PRESENCE comes from the real source computing it from real AMD hardware
        // (GpuGating.IsAmd) — never forced on a fake seam.
        var t = Build(amdPresenceHw: DesktopAmd());
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        // Not applied — would be dishonest.
        r.IsOk.Should().BeFalse();
        // Explicit, distinct honest code.
        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuAmdTuningUnavailable);
        // Distinct from the generic SDK error (which could mean anything).
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakSdkUnavailable,
            because: "must be distinct from a generic SDK unavailable");
        // Distinct from hardware gating (non-AMD machines stay silent — this one DOES apply).
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakNotApplicable,
            because: "must not look like the tweak does not apply to this hardware");
        // No privileged write attempted.
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- CAPABILITY PROBE (hardware-coverage doctrine): a driver that says "ok" and writes nothing. ---
    // This is the whole point of the probe: we cannot own 2015-2026 hardware, so support is
    // proven per device. A locked / OEM-capped / mobile-ish card whose driver reports success
    // and changes nothing must be REFUSED, not credited with a fake undervolt.

    [Fact]
    public async Task Apply_refuses_when_the_identity_probe_write_does_not_take_nvidia()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        // 1st read = capture (15). 2nd read = the probe's read-back, and the card did not
        // take the identity write (still stock 0) → the write path is not live here.
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(15), Result<int>.Ok(0));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnsupportedOnThisGpu);
        // Nothing was tuned: the requested offset never reached the helper.
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == -120), Arg.Any<CancellationToken>());
        // ...and no stability test was run on a GPU we never touched.
        await _stability.DidNotReceive().RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_refuses_when_the_identity_probe_write_does_not_take_amd()
    {
        var t = Build(SafeTarget(coreOffsetMhz: -100));
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        // 1st read = capture (2900). 2nd = probe read-back: the card ignored the write.
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
        // The probe must be a NO-OP for the hardware: it writes the value it just read.
        var t = Build(SafeTarget(coreOffsetMhz: -120));
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(15)); // constant → probe passes

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // The probe wrote 15 (the captured original), i.e. the current value onto itself.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 15), Arg.Any<CancellationToken>());
    }

    // --- Revert restores the captured original. ---

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
