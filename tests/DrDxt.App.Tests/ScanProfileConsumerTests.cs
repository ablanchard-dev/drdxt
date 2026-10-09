using System;
using System.Collections.Generic;
using System.Linq;
using DrDxt.App.Profiles;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M6 — ScanProfileConsumer: per-chip profile recommendation → executed plan + clamped GPU
// target overrides + honest surfaced evidence.
//
// Covers: profile→TweakPlan mapping (fix-misconfig first, deduped, AppliesTo-gated, GPU
// excluded from the executed batch); GPU parameter clamping to GpuSafeBounds at the front
// door (incl. an out-of-bounds value clamped + noted); unknown-chip → generic-only no chip
// guess; failed recommendation → safe no-op; Tested=false surfaced as not-verified; and an
// end-to-end pass over the real foundation matcher + catalog.
// -----------------------------------------------------------------------
public sealed class ScanProfileConsumerTests
{
    // ---- fakes / builders ----

    private static readonly GpuTuningTarget DefaultTuning = new(0, -100, 0);
    private static readonly GpuPowerLimitTarget DefaultPower = new(0, 90);

    private static GpuTuningTargetSource Source() => new(DefaultTuning, DefaultPower);

    private static ITweak Tweak(string id, TweakCategory cat, bool applies = true)
    {
        var t = Substitute.For<ITweak>();
        t.Id.Returns(id);
        t.Category.Returns(cat);
        t.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(applies);
        return t;
    }

    private static ITweakCatalog Catalog(params ITweak[] tweaks)
    {
        var c = Substitute.For<ITweakCatalog>();
        // Unknown ids fail-closed (sealed Result<T> would otherwise resolve to null).
        c.Get(Arg.Any<string>()).Returns(_ => Result<ITweak>.Fail("TWEAK.NOT_FOUND", "unknown"));
        foreach (var t in tweaks)
        {
            var captured = t;
            c.Get(captured.Id).Returns(Result<ITweak>.Ok(captured));
        }
        c.All().Returns(tweaks);
        return c;
    }

    private static IChipProfileMatcher Matcher(params ChipProfile[] profiles)
    {
        var m = Substitute.For<IChipProfileMatcher>();
        var classification = new ChipClassification(false, false, Array.Empty<string>());
        m.Recommend(Arg.Any<HardwareInventory>())
         .Returns(Result<ProfileRecommendation>.Ok(new ProfileRecommendation(profiles, classification)));
        return m;
    }

    private static GainEvidence Ev(string claim = "claim", string src = "src",
        GainConfidence c = GainConfidence.Sourced, SafetyLevel s = SafetyLevel.Safe) =>
        new(claim, src, c, s);

    private static ChipProfile Profile(string id, ChipFamily fam, bool tested, params ProfileTweakSelection[] sels) =>
        new(id, id + ".name", fam, sels, tested, "summary");

    private static ProfileTweakSelection Sel(string tweakId, ProfileIntent intent,
        ProfileParameter[]? prms = null, GainEvidence? ev = null) =>
        new(tweakId, intent, prms ?? Array.Empty<ProfileParameter>(), ev ?? Ev());

    private static HardwareInventory AnyHw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 12L << 30, "1", TriState.On) },
        new[] { new MemoryModule("p", "m", 6000, 6000) },
        new MotherboardInfo("b", "p"), new BiosInfo("1", null, true), ChassisType.Desktop);

    private static HardwareInventory AmdGpuHw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("AMD Radeon RX 7800 XT", "AMD", 16L << 30, "24.1.1", TriState.On) },
        new[] { new MemoryModule("p", "m", 6000, 6000) },
        new MotherboardInfo("b", "p"), new BiosInfo("1", null, true), ChassisType.Desktop);

    private static SettingsState AnySt() => new(
        TriState.On, TriState.On, "g", TriState.On, TriState.On, Array.Empty<int>(), TriState.On);

    // ====================== profile → plan mapping ======================

    [Fact]
    public void Plan_is_profile_selections_fix_misconfig_first_and_excludes_gpu_tuning()
    {
        var catalog = Catalog(
            Tweak("latency.reflex", TweakCategory.Latency),
            Tweak("fixit.power_plan", TweakCategory.FixIt),
            Tweak("gpu.power_limit", TweakCategory.GpuTuning));
        var profile = Profile("p", ChipFamily.NvidiaOptionA, tested: false,
            Sel("latency.reflex", ProfileIntent.Latency),
            Sel("fixit.power_plan", ProfileIntent.FixMisconfig),
            Sel("gpu.power_limit", ProfileIntent.GpuTune, new[] { new ProfileParameter("PowerLimitPct", 90, "%") }));
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        // fix-misconfig first, then latency; GPU tuning is not in the executed batch.
        r.Plan.TweakIds.Should().Equal("fixit.power_plan", "latency.reflex");
        r.Items.Single(i => i.TweakId == "gpu.power_limit").InExecutedPlan.Should().BeFalse();
    }

    [Fact]
    public void Plan_dedupes_a_tweak_selected_by_multiple_profiles()
    {
        var catalog = Catalog(Tweak("fixit.power_plan", TweakCategory.FixIt));
        var generic = Profile("generic", ChipFamily.GenericSafe, false, Sel("fixit.power_plan", ProfileIntent.FixMisconfig));
        var amd = Profile("amd", ChipFamily.AmdX3dDualCcd, false, Sel("fixit.power_plan", ProfileIntent.FixMisconfig));
        var sut = new ScanProfileConsumer(Matcher(generic, amd), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        r.Plan.TweakIds.Should().ContainSingle().Which.Should().Be("fixit.power_plan");
        r.Items.Count(i => i.TweakId == "fixit.power_plan").Should().Be(1);
    }

    [Fact]
    public void Non_applicable_selection_is_dropped_from_plan_but_surfaced_with_a_note()
    {
        var catalog = Catalog(Tweak("fixit.x3d_ccd", TweakCategory.FixIt, applies: false));
        var profile = Profile("amd", ChipFamily.AmdX3dDualCcd, false, Sel("fixit.x3d_ccd", ProfileIntent.FixMisconfig));
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        r.Plan.TweakIds.Should().BeEmpty();
        var item = r.Items.Single(i => i.TweakId == "fixit.x3d_ccd");
        item.InExecutedPlan.Should().BeFalse();
        item.AdjustmentNote.Should().Contain("Not applicable");
    }

    // ====================== GPU param clamping (front door) ======================

    [Fact]
    public void Out_of_bounds_power_limit_is_clamped_to_safe_bounds_before_reaching_the_target()
    {
        var catalog = Catalog(Tweak("gpu.power_limit", TweakCategory.GpuTuning));
        var profile = Profile("nv", ChipFamily.NvidiaOptionA, false,
            Sel("gpu.power_limit", ProfileIntent.GpuTune, new[] { new ProfileParameter("PowerLimitPct", 30, "%") }));
        var source = Source();
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, source);

        var r = sut.Consume(AnyHw(), AnySt());

        // 30% (below floor) is raised to the safe floor — never the dangerous value.
        r.GpuPowerLimit.PowerLimitPct.Should().Be(GpuSafeBounds.MinPowerLimitPct);
        source.PowerLimit.PowerLimitPct.Should().Be(GpuSafeBounds.MinPowerLimitPct); // override committed
        r.Items.Single(i => i.TweakId == "gpu.power_limit").AdjustmentNote.Should().Contain("clamped");
    }

    [Fact]
    public void Out_of_bounds_core_offset_is_clamped_to_safe_bounds_before_reaching_the_target()
    {
        var catalog = Catalog(Tweak("gpu.undervolt", TweakCategory.GpuTuning));
        var profile = Profile("nv", ChipFamily.NvidiaOptionA, false,
            Sel("gpu.undervolt", ProfileIntent.GpuTune, new[] { new ProfileParameter("CoreOffsetMhz", 500, "MHz") }));
        var source = Source();
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, source);

        var r = sut.Consume(AnyHw(), AnySt());

        // +500 MHz (over the band) is pulled down to the conservative max.
        r.GpuTuning.CoreOffsetMhz.Should().Be(GpuSafeBounds.MaxCoreOffsetMhz);
        source.Tuning.CoreOffsetMhz.Should().Be(GpuSafeBounds.MaxCoreOffsetMhz);
        r.Items.Single(i => i.TweakId == "gpu.undervolt").AdjustmentNote.Should().Contain("clamped");
    }

    [Fact]
    public void In_bounds_core_offset_overrides_the_default_without_a_clamp_note()
    {
        var catalog = Catalog(Tweak("gpu.undervolt", TweakCategory.GpuTuning));
        var profile = Profile("nv", ChipFamily.NvidiaOptionA, false,
            Sel("gpu.undervolt", ProfileIntent.GpuTune, new[] { new ProfileParameter("CoreOffsetMhz", 75, "MHz") }));
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        r.GpuTuning.CoreOffsetMhz.Should().Be(75); // overrode the -100 default, in bounds
        r.Items.Single(i => i.TweakId == "gpu.undervolt").AdjustmentNote.Should().NotContain("clamped");
    }

    // ====================== separate GPU plan (dedicated step) ======================

    [Fact]
    public void Applicable_gpu_selections_go_in_the_separate_gpu_plan_not_the_safe_plan()
    {
        var catalog = Catalog(
            Tweak("fixit.power_plan", TweakCategory.FixIt),
            Tweak("gpu.undervolt", TweakCategory.GpuTuning),
            Tweak("gpu.power_limit", TweakCategory.GpuTuning));
        var profile = Profile("nv", ChipFamily.NvidiaOptionA, false,
            Sel("fixit.power_plan", ProfileIntent.FixMisconfig),
            Sel("gpu.undervolt", ProfileIntent.GpuTune, new[] { new ProfileParameter("CoreOffsetMhz", 50, "MHz") }),
            Sel("gpu.power_limit", ProfileIntent.GpuTune, new[] { new ProfileParameter("PowerLimitPct", 90, "%") }));
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        // The safe batch carries the NON-GPU tweak only.
        r.Plan.TweakIds.Should().Equal("fixit.power_plan");
        // The GPU tweaks are their own plan — run by the dedicated, isolated step.
        r.HasGpuPlan.Should().BeTrue();
        r.GpuPlan.TweakIds.Should().BeEquivalentTo("gpu.undervolt", "gpu.power_limit");
        r.GpuPlan.TweakIds.Should().NotContain("fixit.power_plan");
    }

    [Fact]
    public void The_gpu_note_promises_no_dedicated_step_and_says_the_measured_gain_rule()
    {
        // 14/09/2026 — la note disait « applied in a dedicated step » alors que la decision produit
        // interdit d'ecrire sur la carte depuis l'App. 06/10/2026 (L-GPU) : ouverte machine par machine, la carte
        // passe par Optimiser et ne garde un reglage qu'avec un gain mesure.
        var catalog = Catalog(Tweak("gpu.undervolt", TweakCategory.GpuTuning));
        var profile = Profile("nv", ChipFamily.NvidiaOptionA, false,
            Sel("gpu.undervolt", ProfileIntent.GpuTune, new[] { new ProfileParameter("CoreOffsetMhz", 50, "MHz") }));
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, Source());

        var note = sut.Consume(AnyHw(), AnySt()).Items.Single(i => i.TweakId == "gpu.undervolt").AdjustmentNote;

        note.Should().Contain("kept only with a measured gain").And.NotContain("dedicated step");
    }

    [Fact]
    public void Unsupported_gpu_drops_the_gpu_selection_so_there_is_no_gpu_plan()
    {
        // The GPU tweak does not AppliesTo this machine (e.g. iGPU / laptop / handheld) → it is
        // surfaced with a "not applicable" note but never placed in the GPU plan → no GPU batch.
        var catalog = Catalog(Tweak("gpu.undervolt", TweakCategory.GpuTuning, applies: false));
        var profile = Profile("nv", ChipFamily.NvidiaOptionA, false,
            Sel("gpu.undervolt", ProfileIntent.GpuTune, new[] { new ProfileParameter("CoreOffsetMhz", 50, "MHz") }));
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        r.HasGpuPlan.Should().BeFalse();
        r.GpuPlan.TweakIds.Should().BeEmpty();
        var item = r.Items.Single(i => i.TweakId == "gpu.undervolt");
        item.InExecutedPlan.Should().BeFalse();
        item.AdjustmentNote.Should().Contain("Not applicable");
    }

    [Fact]
    public void Failed_recommendation_has_no_gpu_plan()
    {
        var catalog = Catalog(Tweak("gpu.undervolt", TweakCategory.GpuTuning));
        var matcher = Substitute.For<IChipProfileMatcher>();
        matcher.Recommend(Arg.Any<HardwareInventory>())
               .Returns(Result<ProfileRecommendation>.Fail(ErrorCodes.ProfileInputInvalid, "bad"));
        var sut = new ScanProfileConsumer(matcher, catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        r.HasGpuPlan.Should().BeFalse();
        r.GpuPlan.TweakIds.Should().BeEmpty();
    }

    // ====================== unknown chip / fail-closed ======================

    [Fact]
    public void Unknown_chip_yields_only_the_generic_baseline_with_no_chip_specific_guess()
    {
        var catalog = Catalog(
            Tweak("fixit.power_plan", TweakCategory.FixIt),
            Tweak("latency.vrr_vsync_cap", TweakCategory.Latency));
        // The matcher returns only the generic profile (its conservative behaviour for an
        // unrecognised chip) — no AMD/NVIDIA chip-specific profile, no GPU selection.
        var generic = Profile("generic", ChipFamily.GenericSafe, false,
            Sel("fixit.power_plan", ProfileIntent.FixMisconfig),
            Sel("latency.vrr_vsync_cap", ProfileIntent.Latency));
        var sut = new ScanProfileConsumer(Matcher(generic), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        r.Plan.TweakIds.Should().Equal("fixit.power_plan", "latency.vrr_vsync_cap");
        r.Plan.TweakIds.Should().NotContain("fixit.x3d_ccd");
        // No GPU selection → the targets stay at the conservative defaults (no guess).
        r.GpuTuning.Should().Be(DefaultTuning);
        r.GpuPowerLimit.Should().Be(DefaultPower);
    }

    [Fact]
    public void Failed_recommendation_is_a_safe_no_op()
    {
        var catalog = Catalog(Tweak("fixit.power_plan", TweakCategory.FixIt));
        var matcher = Substitute.For<IChipProfileMatcher>();
        matcher.Recommend(Arg.Any<HardwareInventory>())
               .Returns(Result<ProfileRecommendation>.Fail(ErrorCodes.ProfileInputInvalid, "bad"));
        var source = Source();
        var sut = new ScanProfileConsumer(matcher, catalog, source);

        var r = sut.Consume(AnyHw(), AnySt());

        r.HasPlan.Should().BeFalse();
        r.Plan.TweakIds.Should().BeEmpty();
        r.Items.Should().BeEmpty();
        r.GpuTuning.Should().Be(DefaultTuning);
        source.Tuning.Should().Be(DefaultTuning); // reset, never overridden on a no-op
    }

    // ====================== honest surface (evidence + verified state) ======================

    [Fact]
    public void Each_item_carries_its_gain_evidence()
    {
        var catalog = Catalog(Tweak("fixit.power_plan", TweakCategory.FixIt));
        var profile = Profile("generic", ChipFamily.GenericSafe, false,
            Sel("fixit.power_plan", ProfileIntent.FixMisconfig,
                ev: Ev("cooler, steadier 1% lows", "CAHIER §4", GainConfidence.Sourced, SafetyLevel.Safe)));
        var sut = new ScanProfileConsumer(Matcher(profile), catalog, Source());

        var item = sut.Consume(AnyHw(), AnySt()).Items.Single();

        item.Claim.Should().Be("cooler, steadier 1% lows");
        item.Source.Should().Be("CAHIER §4");
        item.Confidence.Should().Be(GainConfidence.Sourced);
        item.Safety.Should().Be(SafetyLevel.Safe);
    }

    [Fact]
    public void Tested_flag_is_surfaced_per_item_as_verified_state()
    {
        var catalog = Catalog(
            Tweak("fixit.power_plan", TweakCategory.FixIt),
            Tweak("fixit.x3d_ccd", TweakCategory.FixIt));
        var untested = Profile("generic", ChipFamily.GenericSafe, tested: false,
            Sel("fixit.power_plan", ProfileIntent.FixMisconfig));
        var validated = Profile("amd", ChipFamily.AmdX3dDualCcd, tested: true,
            Sel("fixit.x3d_ccd", ProfileIntent.FixMisconfig));
        var sut = new ScanProfileConsumer(Matcher(untested, validated), catalog, Source());

        var r = sut.Consume(AnyHw(), AnySt());

        r.Items.Single(i => i.TweakId == "fixit.power_plan").IsVerified.Should().BeFalse(); // "non vérifié"
        r.Items.Single(i => i.TweakId == "fixit.x3d_ccd").IsVerified.Should().BeTrue();
    }

    // ====================== AMD-GPU presence fed from the scanned hardware ======================
    // Regression guard for the honest "AMD GPU detected — tuning coming soon" status. Consume must
    // feed the AMD-presence HARDWARE fact onto the source so the GPU tweaks can surface it under
    // real prod wiring — the ADLX seam cannot (its prod impl reports unavailable on every machine),
    // and that exact gap once made the silent false-green a false-positive in tests. These guard the
    // Consume→source link itself, not just the source→tweak link covered by GpuAmdHonestStatusTests.

    [Fact]
    public void Consume_sets_amd_gpu_presence_on_the_source_from_the_scanned_hardware()
    {
        var source = Source();
        var sut = new ScanProfileConsumer(Matcher(), Catalog(), source);

        sut.Consume(AmdGpuHw(), AnySt());

        source.IsAmdGpuPresent.Should().BeTrue();
    }

    [Fact]
    public void Consume_leaves_amd_gpu_presence_false_on_a_non_amd_machine()
    {
        var source = Source();
        var sut = new ScanProfileConsumer(Matcher(), Catalog(), source);

        sut.Consume(AnyHw(), AnySt()); // NVIDIA GPU only

        source.IsAmdGpuPresent.Should().BeFalse();
    }

    [Fact]
    public void Consume_sets_amd_gpu_presence_even_when_the_recommendation_fails()
    {
        // Presence is a fact of the hardware, not the profile match — it must be recorded even on
        // the fail-closed no-op path, so the honest AMD status still fires for an unrecognised chip.
        var source = Source();
        var matcher = Substitute.For<IChipProfileMatcher>();
        matcher.Recommend(Arg.Any<HardwareInventory>())
               .Returns(Result<ProfileRecommendation>.Fail(ErrorCodes.ProfileInputInvalid, "bad"));
        var sut = new ScanProfileConsumer(matcher, Catalog(), source);

        sut.Consume(AmdGpuHw(), AnySt());

        source.IsAmdGpuPresent.Should().BeTrue();
    }

    // ====================== end-to-end over the real foundation ======================

    [Fact]
    public void Real_foundation_profiles_compose_for_an_amd_x3d_nvidia_desktop()
    {
        var catalog = Catalog(
            Tweak("fixit.power_plan", TweakCategory.FixIt),
            Tweak("fixit.x3d_ccd", TweakCategory.FixIt),
            Tweak("latency.reflex", TweakCategory.Latency),
            Tweak("latency.vrr_vsync_cap", TweakCategory.Latency),
            Tweak("latency.render_queue_cap", TweakCategory.Latency),
            Tweak("gpu.power_limit", TweakCategory.GpuTuning),
            Tweak("gpu.undervolt", TweakCategory.GpuTuning));
        var matcher = new ChipProfileMatcher(new ChipProfileCatalog());
        var source = Source();
        var sut = new ScanProfileConsumer(matcher, catalog, source);

        var hw = new HardwareInventory(
            new CpuInfo("AMD Ryzen 9 7950X3D 16-Core Processor", "AuthenticAMD", 16, 32, true, 2),
            new[] { new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, "560.94", TriState.On) },
            new[] { new MemoryModule("p", "m", 6000, 6000) },
            new MotherboardInfo("ASUS", "X670E"), new BiosInfo("1", null, true), ChassisType.Desktop);

        var r = sut.Consume(hw, AnySt());

        // executed batch = applicable NON-GPU selections from generic + AMD-X3D + NVIDIA.
        r.Plan.TweakIds.Should().Contain(new[] { "fixit.power_plan", "fixit.x3d_ccd" });
        r.Plan.TweakIds.Should().NotContain("gpu.power_limit");
        r.Plan.TweakIds.Should().NotContain("gpu.undervolt");
        // GPU targets overridden from the NVIDIA Option-A profile (in-bounds: 90 %). 08/10/2026 (V2) : le décalage
        // d'horloge n'est plus proposé par aucun profil (règle du 29/09, chiffre non reproductible).
        r.GpuPowerLimit.PowerLimitPct.Should().Be(90);
        r.Items.Should().NotContain(i => i.TweakId == "gpu.undervolt");
        // all foundation profiles are Tested=false → every surfaced line is "non vérifié".
        r.Items.Should().OnlyContain(i => !i.IsVerified);
        r.Items.Should().NotBeEmpty();
    }

    // ====================== non lu n'est pas « rien a faire » ======================

    /// 17/09/2026, mesure sur la machine de reference (`scanprobe --plan`) : les DIX leviers du plan
    /// sortent « Not applicable to this machine: skipped », et rien n'atteint l'ecran. Trois
    /// d'entre eux ne sont pas inapplicables : leurs valeurs n'existent tout simplement pas dans
    /// le registre (`AllowAutoGameMode`, `HwSchMode` : absentes), donc l'etat lu est `Unknown`.
    /// `AppliesTo` rend alors `false`, exactement comme pour une machine deja reglee -- et le
    /// client lit « ca ne te concerne pas » la ou la verite est « je n'ai pas su regarder ».
    /// C'est le meme defaut que le produit a deja corrige ailleurs : `Auto` vaut UNKNOWN, pas OK.
    [Fact]
    public void Un_levier_dont_l_etat_n_a_PAS_pu_etre_lu_ne_se_dit_pas_inapplicable()
    {
        var muet = Tweak("latency.gameModeOn", TweakCategory.Latency, applies: false);
        muet.EtatNonLu(Arg.Any<SettingsState>()).Returns(true);
        var sut = new ScanProfileConsumer(
            Matcher(Profile("p", ChipFamily.NvidiaOptionA, tested: true,
                Sel("latency.gameModeOn", ProfileIntent.Latency))),
            Catalog(muet),
            Source());

        var r = sut.Consume(AnyHw(), AnySt());

        var item = r.Items.Single(i => i.TweakId == "latency.gameModeOn");
        item.InExecutedPlan.Should().BeFalse("on n'ecrit rien sur ce qu'on n'a pas lu");
        item.AdjustmentNote.Should().NotContain("Not applicable to this machine");
        item.AdjustmentNote.Should().Contain("could not be read");
    }
}
