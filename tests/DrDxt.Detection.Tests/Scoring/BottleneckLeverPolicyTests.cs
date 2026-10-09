using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

// -----------------------------------------------------------------------
// L1.3 — la mesure decide ce qu'on a le droit de PROMETTRE.
//
// La ligne a ne jamais franchir, et que ces tests gardent : une suppression retire la promesse de
// gain, jamais le constat ni la penalite. XMP desactive reste XMP desactive sur une machine
// GPU-bound. Si le /100 se mettait a dependre de la mesure, le score d'une machine changerait
// selon qu'un jeu tournait ou non au moment du scan.
// -----------------------------------------------------------------------
public sealed class BottleneckLeverPolicyTests
{
    private const string Xmp = BottleneckLeverPolicy.LeverIds.Xmp;
    private const string X3d = BottleneckLeverPolicy.LeverIds.X3dCcd;
    private const string ReBar = BottleneckLeverPolicy.LeverIds.ReBar;
    private const string Driver = BottleneckLeverPolicy.LeverIds.GpuDriver;
    private const string Vrr = BottleneckLeverPolicy.LeverIds.Vrr;

    private static BottleneckVerdict V(BottleneckState state, bool provisional = false) =>
        new(state, BottleneckConfidence.High,
            "A reason long enough to be shown to a player as it is.", provisional);

    [Fact]
    public void No_measurement_suppresses_nothing()
    {
        // Ne pas savoir n'est pas une raison de taire un reglage : c'est une raison de ne pas
        // promettre. Et la promesse tombe deja par AllowsMeasuredGainClaim.
        BottleneckLeverPolicy.SuppressionReason(Xmp, null).Should().BeNull();
        BottleneckLeverPolicy.SuppressionReason(X3d, null).Should().BeNull();
    }

    [Fact]
    public void A_provisional_verdict_suppresses_nothing()
    {
        // Supprimer un levier est une affirmation forte : « ca ne t'apportera rien » se trompe
        // aussi lourdement que « ca va te gagner 10 % ». La spec les reserve a 3 runs.
        BottleneckLeverPolicy.SuppressionReason(X3d, V(BottleneckState.GpuBound, provisional: true))
            .Should().BeNull();
    }

    [Fact]
    public void On_a_gpu_bound_machine_the_cpu_and_ram_levers_stop_being_sold_as_frames()
    {
        var gpuBound = V(BottleneckState.GpuBound);

        BottleneckLeverPolicy.SuppressionReason(X3d, gpuBound).Should().NotBeNull();
        BottleneckLeverPolicy.SuppressionReason(Xmp, gpuBound).Should().NotBeNull();
    }

    [Fact]
    public void A_gpu_bound_machine_keeps_its_gpu_side_levers()
    {
        // Ce que la ligne GPU_BOUND de la matrice ne supprime pas. Tout supprimer serait aussi
        // faux que ne rien supprimer.
        var gpuBound = V(BottleneckState.GpuBound);

        BottleneckLeverPolicy.SuppressionReason(ReBar, gpuBound).Should().BeNull();
        BottleneckLeverPolicy.SuppressionReason(Driver, gpuBound).Should().BeNull();
    }

    [Fact]
    public void On_a_cpu_bound_machine_the_graphics_levers_stop_being_sold_as_frames()
    {
        foreach (var state in new[] { BottleneckState.CpuBoundStrong, BottleneckState.CpuBoundOrAppBound })
        {
            BottleneckLeverPolicy.SuppressionReason(ReBar, V(state)).Should().NotBeNull(state.ToString());
            BottleneckLeverPolicy.SuppressionReason(Driver, V(state)).Should().NotBeNull(state.ToString());
        }
    }

    [Fact]
    public void A_cpu_bound_machine_keeps_its_memory_profile_lever()
    {
        // Contre-intuitif et voulu : sur une machine limitee par le CPU, le profil memoire est
        // justement un des rares leviers qui pese. Le supprimer « par symetrie » couterait des
        // images reelles.
        BottleneckLeverPolicy.SuppressionReason(Xmp, V(BottleneckState.CpuBoundStrong))
            .Should().BeNull();
    }

    [Fact]
    public void Latency_levers_survive_a_cpu_bound_verdict()
    {
        // La matrice recommande VRR+cap sur une machine CPU-bound. Les ranger cote GPU les
        // aurait supprimes, et c'est l'erreur exacte que `IsGpuSide` evite.
        BottleneckLeverPolicy.SuppressionReason(Vrr, V(BottleneckState.CpuBoundStrong))
            .Should().BeNull();
    }

    [Fact]
    public void Under_memory_pressure_nothing_at_all_is_sold_as_frames()
    {
        var pressure = V(BottleneckState.RamCommitPressure);

        foreach (var lever in new[] { Xmp, X3d, ReBar, Driver, Vrr })
            BottleneckLeverPolicy.SuppressionReason(lever, pressure).Should().NotBeNull(lever);
    }

    [Fact]
    public void Under_a_cap_no_lever_can_promise_a_frame_rate_gain()
    {
        // La moyenne FPS ne peut pas bouger : aucun gain n'est prouvable, pour aucun levier.
        var capped = V(BottleneckState.CappedOrVsyncLimited);

        foreach (var lever in new[] { Xmp, X3d, ReBar, Driver, Vrr })
            BottleneckLeverPolicy.SuppressionReason(lever, capped).Should().NotBeNull(lever);
    }

    [Fact]
    public void A_throttling_gpu_is_not_asked_for_more_but_its_driver_still_is()
    {
        var hot = V(BottleneckState.ThermalOrPowerThrottle);

        BottleneckLeverPolicy.SuppressionReason(ReBar, hot).Should().NotBeNull();
        BottleneckLeverPolicy.SuppressionReason(Driver, hot)
            .Should().BeNull("un bug de ventilation se corrige justement par une mise a jour");
    }

    [Fact]
    public void A_pacing_problem_suppresses_raw_throughput_but_not_the_pacing_fixes()
    {
        var pacing = V(BottleneckState.FramePacing);

        BottleneckLeverPolicy.SuppressionReason(Xmp, pacing).Should().NotBeNull();
        BottleneckLeverPolicy.SuppressionReason(X3d, pacing).Should().NotBeNull();
        BottleneckLeverPolicy.SuppressionReason(Vrr, pacing)
            .Should().BeNull("VRR+cap EST la reponse a une cadence irreguliere");
        BottleneckLeverPolicy.SuppressionReason(Driver, pacing)
            .Should().BeNull("un bug de cadence connu se corrige par le pilote");
    }

    [Fact]
    public void An_unknown_verdict_suppresses_no_lever()
    {
        // UNKNOWN_PARTIAL retire la promesse CHIFFREE, pas les corrections de configuration.
        foreach (var lever in new[] { Xmp, X3d, ReBar, Driver, Vrr })
            BottleneckLeverPolicy.SuppressionReason(lever, V(BottleneckState.UnknownPartial))
                .Should().BeNull(lever);
    }

    [Fact]
    public void Every_suppression_carries_the_measured_reason_that_caused_it()
    {
        // Un levier supprime sans motif est un levier supprime sans recours.
        var verdict = V(BottleneckState.GpuBound);

        BottleneckLeverPolicy.SuppressionReason(X3d, verdict)
            .Should().Contain(verdict.Reason);
    }

    [Fact]
    public void An_unknown_lever_id_is_never_silently_suppressed()
    {
        // Une faute de frappe dans un identifiant doit rater la suppression, pas en inventer une
        // sur un levier qui n'existe pas.
        BottleneckLeverPolicy.SuppressionReason("xpm", V(BottleneckState.GpuBound))
            .Should().BeNull();
    }
}
