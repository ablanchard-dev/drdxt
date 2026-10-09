using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks;

// -----------------------------------------------------------------------
// M2 Phase F4 — TweakCatalog + the HARD anti-blacklist gate (§6 permanent barrier).
//
// The catalog aggregates all ITweak (DI IEnumerable<ITweak>) and implements the
// reconciled ITweakCatalog (Get(id) / All()).
//
//   * Anti-blacklist gate: AllTweakIds contains none of the banned placebo ids.
//   * Every Latency / GpuTuning / FixIt tweak has RequiresRestorePoint == true.
//   * The catalog resolves via DI.
//
// The anti-blacklist gate is a PERMANENT barrier: it is impossible to introduce a
// banned/placebo tweak without failing this test.
// -----------------------------------------------------------------------
public sealed class TweakCatalogTests
{
    // §6 blacklist — these placebo / harmful ids may never appear in the catalog.
    // The CPU-domain entries are the myths debunked by the project's CPU research (unpublished)
    // (Realtime priority, MMCSS/SystemResponsiveness=0, blanket C-state disable, blanket
    // affinity pinning, blanket core-unpark, useplatformclock/HPET). NS-3 concluded the
    // granular powercfg "levers" (PROCTHROTTLEMIN/PERFBOOSTMODE) are no-ops on HWP CPUs and
    // redundant with SanePowerPlan, so instead of shipping placebos we make the myths
    // permanently unshippable here (they are already structurally denied by OpAllowlist's
    // default-DENY too — this is the belt-and-suspenders documentation guard).
    // The last three were named by the project's OS-latency research (unpublished) as myths to lock, and
    // were never added — found 2026-09-01 while auditing a public 74-script optimisation pack
    // that writes all three (MSISupported=1 forced per-device, SwapEffectUpgradeEnable=0,
    // PowerThrottlingOff). The other two ids that recommendation listed already exist here
    // under different names: mmcss.systemresponsiveness -> cpu.mmcss_disable, and
    // clock.useplatformclock -> timer.useplatformclock.
    private static readonly string[] BannedTweakIds =
    {
        "net.nagle", "net.tcpack", "net.throttlingindex", "debloat.services",
        "polling.8000hz", "timer.global", "rebar.force_global", "ecores.disable_default",
        "cpu.priority_realtime", "cpu.mmcss_disable", "cpu.cstates_disable_all",
        "cpu.affinity_pin_blanket", "cpu.unpark_all", "timer.useplatformclock",
        "msi.force", "fso.disable_global", "powerthrottling.global_off",
    };

    private static ITweak Fake(string id, TweakCategory category, bool requiresRestorePoint = true)
    {
        var t = Substitute.For<ITweak>();
        t.Id.Returns(id);
        t.Category.Returns(category);
        t.RequiresRestorePoint.Returns(requiresRestorePoint);
        return t;
    }

    [Fact]
    public void Catalog_contains_no_blacklisted_tweak_ids()
    {
        // Include a couple of legitimate tweaks; assert none of the banned ids leak in.
        var catalog = new TweakCatalog(new[]
        {
            Fake("latency.reflex", TweakCategory.Latency),
            Fake("gpu.undervolt", TweakCategory.GpuTuning),
            Fake("fixit.x3d_ccd", TweakCategory.FixIt),
        });

        catalog.AllTweakIds.Should().NotContain(BannedTweakIds); // §6 hard barrier
    }

    [Fact]
    public void A_registered_cpu_myth_tweak_is_caught_by_the_barrier()
    {
        // Proves the §6 gate actually BITES on the debunked CPU myths (cpu-all.md §3), not
        // just that the list holds strings: a catalog that registered one must intersect the
        // ban-list, so the NotContain assertion above would fail for it.
        var catalog = new TweakCatalog(new[]
        {
            Fake("latency.reflex", TweakCategory.Latency),
            Fake("cpu.priority_realtime", TweakCategory.FixIt), // Realtime priority — banned
        });

        catalog.AllTweakIds.Intersect(BannedTweakIds).Should().Contain("cpu.priority_realtime");
    }

    [Fact]
    public void Every_latency_gpu_and_fixit_tweak_requires_a_restore_point()
    {
        var catalog = new TweakCatalog(new[]
        {
            Fake("latency.reflex", TweakCategory.Latency),
            Fake("gpu.undervolt", TweakCategory.GpuTuning),
            Fake("fixit.power_plan", TweakCategory.FixIt),
        });

        catalog.All()
            .Where(t => t.Category is TweakCategory.Latency or TweakCategory.GpuTuning or TweakCategory.FixIt)
            .All(t => t.RequiresRestorePoint).Should().BeTrue();
    }

    [Fact]
    public void Get_returns_the_tweak_by_id()
    {
        var reflex = Fake("latency.reflex", TweakCategory.Latency);
        var catalog = new TweakCatalog(new[] { reflex });

        var got = catalog.Get("latency.reflex");

        got.IsOk.Should().BeTrue();
        got.Value.Should().BeSameAs(reflex);
    }

    [Fact]
    public void Get_unknown_id_fails_clean()
    {
        var catalog = new TweakCatalog(new[] { Fake("latency.reflex", TweakCategory.Latency) });

        var got = catalog.Get("does.not.exist");

        got.IsOk.Should().BeFalse();
        got.Error!.Value.Code.Should().Be(ErrorCodes.TweakNotApplicable);
    }

    [Fact]
    public void All_exposes_every_registered_tweak()
    {
        var a = Fake("latency.reflex", TweakCategory.Latency);
        var b = Fake("gpu.undervolt", TweakCategory.GpuTuning);
        var catalog = new TweakCatalog(new[] { a, b });

        catalog.All().Should().BeEquivalentTo(new[] { a, b });
        catalog.AllTweakIds.Should().BeEquivalentTo(new[] { "latency.reflex", "gpu.undervolt" });
    }

    [Fact]
    public void Catalog_is_resolvable_via_DI()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Fake("latency.reflex", TweakCategory.Latency));
        services.AddSingleton(Fake("fixit.x3d_ccd", TweakCategory.FixIt));
        services.AddSingleton<ITweakCatalog, TweakCatalog>();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ITweakCatalog>();

        catalog.Should().NotBeNull();
        catalog.All().Should().HaveCount(2);
        // The §6 gate also holds through the resolved interface.
        catalog.All().Select(t => t.Id).Should().NotContain(BannedTweakIds);
    }
}
