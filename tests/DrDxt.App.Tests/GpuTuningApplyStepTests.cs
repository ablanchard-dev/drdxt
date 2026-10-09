using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Profiles;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M6 — GpuTuningApplyStep: the dedicated, isolated GPU-tuning apply action.
//
// Locks the isolation contract at the step boundary (the engine is a fake here so these
// stay fast + deterministic; the real-engine isolation — "a GPU failure never rolls back a
// previously-applied safe batch" + "a regression auto-reverts only the GPU change" — is
// proven end-to-end in DrDxt.Integration.Tests/GpuTuningIsolationTests):
//   * the GPU plan runs as its own engine batch, carrying only the GPU ids (never the safe
//     plan ids) — the structural reason a GPU failure can't touch the safe batch;
//   * an empty GPU plan (unsupported GPU / nothing recommended) is a safe no-op (no engine
//     batch at all);
//   * the live targets are RE-CLAMPED to GpuSafeBounds before applying (defense in depth);
//   * the scan baseline is forwarded so the engine's A/B re-bench / auto-revert is honest;
//   * a clean engine failure is surfaced as a failure (never faked into success).
// -----------------------------------------------------------------------
public sealed class GpuTuningApplyStepTests
{
    private static readonly GpuTuningTarget DefaultTuning = new(0, -100, 0);
    private static readonly GpuPowerLimitTarget DefaultPower = new(0, 90);

    private static GpuTuningTargetSource Source() => new(DefaultTuning, DefaultPower);

    private static ITweakEngine OkEngine()
    {
        var e = Substitute.For<ITweakEngine>();
        e.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
         .Returns(Result<ApplyReport>.Ok(new ApplyReport(Array.Empty<ApplyOutcome>(), Verdict: null)));
        return e;
    }

    private static ScanProfileConsumption Consumption(string[] safePlan, string[] gpuPlan,
        GpuTuningTarget? tuning = null, GpuPowerLimitTarget? power = null) =>
        new(new TweakPlan(safePlan), tuning ?? DefaultTuning, power ?? DefaultPower,
            Array.Empty<ProfileRecommendationItem>())
        {
            GpuPlan = new TweakPlan(gpuPlan),
        };

    // ====================== own, isolated batch ======================

    [Fact]
    public async Task Applies_only_the_gpu_plan_as_its_own_engine_batch()
    {
        var engine = OkEngine();
        var step = new GpuTuningApplyStep(engine, Source());
        // The consumption has both a safe plan and a GPU plan; the step must touch only the GPU one.
        var consumption = Consumption(
            safePlan: new[] { "fixit.power_plan", "latency.reflex" },
            gpuPlan: new[] { "gpu.undervolt", "gpu.power_limit" });

        await step.ApplyAsync(consumption, baseline: null, targetPid: 0, CancellationToken.None);

        await engine.Received(1).ApplyAsync(
            Arg.Is<TweakPlan>(p =>
                p.TweakIds.Count == 2 &&
                p.TweakIds.Contains("gpu.undervolt") && p.TweakIds.Contains("gpu.power_limit") &&
                !p.TweakIds.Contains("fixit.power_plan") && !p.TweakIds.Contains("latency.reflex")),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ====================== unsupported GPU / nothing recommended → no-op ======================

    [Fact]
    public async Task Is_a_safe_no_op_when_no_gpu_tuning_is_recommended()
    {
        var engine = Substitute.For<ITweakEngine>();
        var step = new GpuTuningApplyStep(engine, Source());
        var consumption = Consumption(safePlan: new[] { "fixit.power_plan" }, gpuPlan: Array.Empty<string>());

        var r = await step.ApplyAsync(consumption, baseline: null, targetPid: 0, CancellationToken.None);

        r.IsOk.Should().BeTrue();
        r.Value!.Outcomes.Should().BeEmpty();
        r.Value.Verdict.Should().BeNull();
        // No engine batch is ever created for an unsupported GPU.
        await engine.DidNotReceive().ApplyAsync(
            Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Null_consumption_is_a_safe_no_op()
    {
        var engine = Substitute.For<ITweakEngine>();
        var step = new GpuTuningApplyStep(engine, Source());

        var r = await step.ApplyAsync(consumption: null!, baseline: null, targetPid: 0, CancellationToken.None);

        r.IsOk.Should().BeTrue();
        r.Value!.Outcomes.Should().BeEmpty();
        await engine.DidNotReceive().ApplyAsync(
            Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ====================== re-clamp to GpuSafeBounds (defense in depth) ======================

    [Fact]
    public async Task Reclamps_out_of_bounds_live_targets_to_safe_bounds_before_applying()
    {
        var engine = OkEngine();
        // A source whose live values were somehow pushed OUT of band — the step must pull them
        // back IN band before the engine batch runs (it never trusts the upstream clamp blindly).
        var source = new GpuTuningTargetSource(DefaultTuning, DefaultPower);
        source.Set(new GpuTuningTarget(0, CoreOffsetMhz: 9999, MemoryOffsetMhz: -9999),
                   new GpuPowerLimitTarget(0, PowerLimitPct: 5));
        var step = new GpuTuningApplyStep(engine, source);
        var consumption = Consumption(safePlan: Array.Empty<string>(), gpuPlan: new[] { "gpu.undervolt" });

        await step.ApplyAsync(consumption, baseline: null, targetPid: 0, CancellationToken.None);

        source.Tuning.CoreOffsetMhz.Should().Be(GpuSafeBounds.MaxCoreOffsetMhz);   // 9999 → 100
        source.Tuning.MemoryOffsetMhz.Should().Be(GpuSafeBounds.MinMemoryOffsetMhz); // -9999 → -500
        source.PowerLimit.PowerLimitPct.Should().Be(GpuSafeBounds.MinPowerLimitPct); // 5 → 60
    }

    [Fact]
    public async Task Leaves_already_in_bounds_targets_unchanged()
    {
        var engine = OkEngine();
        var source = new GpuTuningTargetSource(DefaultTuning, DefaultPower);
        source.Set(new GpuTuningTarget(0, CoreOffsetMhz: 75, MemoryOffsetMhz: 100),
                   new GpuPowerLimitTarget(0, PowerLimitPct: 85));
        var step = new GpuTuningApplyStep(engine, source);
        var consumption = Consumption(safePlan: Array.Empty<string>(), gpuPlan: new[] { "gpu.undervolt" });

        await step.ApplyAsync(consumption, baseline: null, targetPid: 0, CancellationToken.None);

        source.Tuning.CoreOffsetMhz.Should().Be(75);
        source.Tuning.MemoryOffsetMhz.Should().Be(100);
        source.PowerLimit.PowerLimitPct.Should().Be(85);
    }

    // ====================== forwards the scan baseline ======================

    [Fact]
    public async Task Forwards_the_scan_baseline_and_pid_to_the_engine()
    {
        var engine = OkEngine();
        var step = new GpuTuningApplyStep(engine, Source());
        var baseline = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var consumption = Consumption(safePlan: Array.Empty<string>(), gpuPlan: new[] { "gpu.undervolt" });

        await step.ApplyAsync(consumption, baseline, targetPid: 7, CancellationToken.None);

        await engine.Received(1).ApplyAsync(
            Arg.Any<TweakPlan>(),
            Arg.Is<BenchmarkResult?>(b => ReferenceEquals(b, baseline)),
            7, Arg.Any<CancellationToken>());
    }

    // ====================== honest failure surface ======================

    [Fact]
    public async Task Surfaces_an_engine_gpu_batch_failure_as_a_failure()
    {
        var engine = Substitute.For<ITweakEngine>();
        engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, "GPU batch reverted"));
        var step = new GpuTuningApplyStep(engine, Source());
        var consumption = Consumption(safePlan: Array.Empty<string>(), gpuPlan: new[] { "gpu.undervolt" });

        var r = await step.ApplyAsync(consumption, baseline: null, targetPid: 0, CancellationToken.None);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
    }
}
