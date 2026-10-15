using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

// -----------------------------------------------------------------------
// Le classifieur de goulot.
//
// Un test par etat, plus les gardes qui font que l'ordre des tests est la politique. Ce qui se
// casse silencieusement ici, si on n'y prend pas garde :
//   * un `?? 0` sur une attente GPU non mesuree rendrait tout le monde GPU-bound ;
//   * classer « qui pace la frame » avant la pression memoire vendrait un overclock a une
//     machine qui gele ;
//   * oublier le cap laisserait le produit affirmer un gain mesure sur une session ou la
//     moyenne FPS ne peut pas bouger -- l'illusion exacte mesuree le 01/09.
// -----------------------------------------------------------------------
public sealed class BottleneckClassifierTests
{
    // Un frametime moyen de 7 ms (≈143 FPS) avec la dispersion qu'on veut. `spread` = 0 donne
    // une cadence parfaitement reguliere, donc CV = 0 : les tests qui ne parlent pas de cadence
    // ne doivent pas tomber par accident dans FRAME_PACING.
    private static IReadOnlyList<double> Frames(double meanMs = 7.0, double spread = 0.0, int n = 600)
    {
        var ft = new List<double>(n);
        for (var i = 0; i < n; i++)
            ft.Add(meanMs + (i % 2 == 0 ? spread : -spread));
        return ft;
    }

    private static BenchmarkResult Result(
        double? gpuBusyMs = 6.6,       // 6,6 / 7,0 ≈ 0,94 ⇒ GPU-bound
        double? gpuWaitRatio = 0.03,
        double? cpuBusyRatio = 0.30,
        double? presentStallMsP95 = 0.2,
        double? renderPresentLatencyMsP95 = 3.0,
        double? animationErrorMsP95 = 0.5,
        double? commitPressure = 0.45,
        double spread = 0.0,
        int runs = 5,
        IReadOnlyList<double>? frames = null) =>
        new(AvgFps: 142.9,
            P1LowFps: 120,
            P01LowFps: 90,
            FrameTimesMs: frames ?? Frames(spread: spread),
            EstimatedPcLatencyMs: 3.0,
            MarginOfError: 1.0,
            Runs: runs,
            MeanGpuBusyMs: gpuBusyMs,
            GpuWaitRatio: gpuWaitRatio,
            CpuBusyRatio: cpuBusyRatio,
            PresentStallMsP95: presentStallMsP95,
            RenderPresentLatencyMsP95: renderPresentLatencyMsP95,
            AnimationErrorMsP95: animationErrorMsP95,
            CommitPressure: commitPressure,
            // Le banc compte chaque série ; un p95 sans compte ne décide rien (règle de l'écran).
            AnimationErrorSamples: (frames ?? Frames(spread: spread)).Count,
            PresentStallSamples: (frames ?? Frames(spread: spread)).Count);

    // ====================== l'aveu d'ignorance ======================

    [Fact]
    public void No_measurement_at_all_is_said_not_guessed()
    {
        var v = BottleneckClassifier.Classify(null);

        v.State.Should().Be(BottleneckState.UnknownPartial);
        v.AllowsMeasuredGainClaim.Should().BeFalse();
    }

    [Fact]
    public void An_unmeasured_gpu_busy_never_becomes_a_confident_state()
    {
        // Six des dix etats dependent de cette grandeur. Sans elle, en nommer un serait inventer.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: null));

        v.State.Should().Be(BottleneckState.UnknownPartial);
        v.Confidence.Should().Be(BottleneckConfidence.High, "« je ne sais pas » est une reponse sure");
        v.Reason.Should().Contain("GPU busy");
    }

    [Fact]
    public void The_band_between_two_states_is_named_as_undecided_not_rounded_to_a_neighbour()
    {
        // 0,78 tombe entre CPU_BOUND (< 0,75) et MIXED (>= 0,80). Choisir le voisin « qui va
        // bien » est exactement ce que ce classifieur existe pour ne plus faire.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.78, gpuWaitRatio: 0.05));

        v.State.Should().Be(BottleneckState.UnknownPartial);
    }

    // ====================== qui pace la frame ======================

    [Fact]
    public void A_saturated_gpu_that_barely_waits_is_gpu_bound()
    {
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95, gpuWaitRatio: 0.03));

        v.State.Should().Be(BottleneckState.GpuBound);
        v.Confidence.Should().Be(BottleneckConfidence.High);
        v.AllowsMeasuredGainClaim.Should().BeTrue();
    }

    [Fact]
    public void An_idle_gpu_that_waits_a_lot_is_strongly_cpu_bound()
    {
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.45, gpuWaitRatio: 0.40));

        v.State.Should().Be(BottleneckState.CpuBoundStrong);
        v.Reason.Should().Contain("not an FPS answer");
    }

    [Fact]
    public void The_middle_cpu_band_is_stated_with_medium_confidence()
    {
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.70, gpuWaitRatio: 0.25));

        v.State.Should().Be(BottleneckState.CpuBoundOrAppBound);
        v.Confidence.Should().Be(BottleneckConfidence.Medium);
    }

    [Fact]
    public void The_mixed_band_says_may_help_instead_of_claiming_the_limit()
    {
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.85, gpuWaitRatio: 0.10));

        v.State.Should().Be(BottleneckState.MixedGpuLeaning);
        v.Confidence.Should().Be(BottleneckConfidence.Medium);
        v.Reason.Should().Contain("MAY help");
    }

    [Fact]
    public void An_unmeasured_gpu_wait_can_never_ACCUSE_the_cpu()
    {
        // La garde qui compte. Un `?? 0` ici aurait fait passer cette machine pour CPU-bound sur
        // une grandeur qui n'a jamais ete lue -- un verdict fabrique, dans les deux sens.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.45, gpuWaitRatio: null));

        v.State.Should().Be(BottleneckState.UnknownPartial);
    }

    [Fact]
    public void An_unmeasured_gpu_wait_still_allows_the_gpu_bound_call()
    {
        // Asymetrie deliberee : le ratio busy seul suffit a dire « le GPU pace la frame »
        // (c'est le comportement d'avant ce classifieur, degrade mais pas faux). Accuser le CPU,
        // lui, demande la preuve que le GPU attendait.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95, gpuWaitRatio: null));

        v.State.Should().Be(BottleneckState.GpuBound);
    }

    // ====================== l'ordre est la politique ======================

    [Fact]
    public void Memory_pressure_beats_a_saturated_gpu()
    {
        // L'inversion la plus couteuse du fichier : cette machine est GPU-bound et a la limite
        // de validation. Repondre « overclocke ton GPU » a quelqu'un dont la machine gele est
        // pire que ne rien repondre.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.96, commitPressure: 0.93));

        v.State.Should().Be(BottleneckState.RamCommitPressure);
        v.Confidence.Should().Be(BottleneckConfidence.High);
        v.Reason.Should().Contain("RELIABILITY");
        v.Reason.Should().Contain("no FPS gain is claimed");
    }

    [Fact]
    public void Memory_pressure_is_said_even_when_the_frame_capture_failed()
    {
        // Elle ne depend d'aucune grandeur de frame -- et une machine qui gele est justement
        // celle dont la capture echoue.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: null, commitPressure: 0.95));

        v.State.Should().Be(BottleneckState.RamCommitPressure);
    }

    [Fact]
    public void A_cap_beats_the_pacing_and_the_limiter_questions()
    {
        // Sous un cap, la moyenne FPS ne bouge pas quoi qu'on regle : illusion mesuree le 01/09,
        // ou ne rien faire « gagnait » plus que l'overclock.
        var cap = new FrameCapState(FrameCapVerdict.ComputedOnAnotherDisplay,
            "The cap (141 fps) was computed on your 144 Hz screen while you play on the 200 Hz one.");

        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.60, spread: 2.0), cap);

        v.State.Should().Be(BottleneckState.CappedOrVsyncLimited);
        v.AllowsMeasuredGainClaim.Should().BeFalse("aucun A/B sur la moyenne n'est valide sous un cap");
    }

    [Fact]
    public void A_coherent_cap_alone_does_not_forbid_measuring()
    {
        // Un cap correct a refresh-3 existe mais ne bride que si le jeu l'atteint. Le compter
        // comme bridant interdirait tout A/B a une machine bien reglee.
        var cap = new FrameCapState(FrameCapVerdict.Coherent, "The cap matches your primary display.");

        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95), cap);

        v.State.Should().Be(BottleneckState.GpuBound);
    }

    [Fact]
    public void Waiting_inside_Present_on_an_unsaturated_gpu_reads_as_capped()
    {
        var v = BottleneckClassifier.Classify(
            Result(gpuBusyMs: 7.0 * 0.55, gpuWaitRatio: 0.40, presentStallMsP95: 3.5));

        v.State.Should().Be(BottleneckState.CappedOrVsyncLimited);
        v.Reason.Should().Contain("Present()");
    }

    [Fact]
    public void A_throttling_gpu_is_not_told_to_take_more_power()
    {
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95), gpuThrottling: true);

        v.State.Should().Be(BottleneckState.ThermalOrPowerThrottle);
        v.Reason.Should().Contain("thermal headroom");
    }

    [Fact]
    public void Thermal_state_is_never_guessed_when_the_telemetry_is_unreadable()
    {
        // La spec range le thermique dans ce qui « needs vendor telemetry ». null = pas lisible.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95), gpuThrottling: null);

        v.State.Should().Be(BottleneckState.GpuBound);
    }

    // ====================== la cadence ======================

    [Fact]
    public void Uneven_frame_times_are_called_out_even_when_the_average_looks_healthy()
    {
        // 143 FPS de moyenne, et injouable : c'est ce que le joueur ressent, et ce qu'une
        // moyenne ne dit jamais.
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95, spread: 1.5));

        v.State.Should().Be(BottleneckState.FramePacing);
        v.Reason.Should().Contain("average");
    }

    [Fact]
    public void Rare_hard_hitches_are_caught_even_when_the_variation_stays_low()
    {
        // Le p99 porte sur le CENTILE, pas sur la frame la plus lente : une seule frame a 3x
        // ne le deplace pas d'un poil (ecrit d'abord comme ca, et le test l'a dit).
        // 989 frames a 7 ms + 11 a 13 ms : le CV reste a ~0,09 (sous le seuil de cadence) alors
        // que le p99/p50 monte a 1,86. C'est exactement le cas que le CV seul manquerait.
        var ft = new List<double>();
        for (var i = 0; i < 989; i++) ft.Add(7.0);
        for (var i = 0; i < 11; i++) ft.Add(13.0);

        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95, frames: ft));

        v.State.Should().Be(BottleneckState.FramePacing);
    }

    [Fact]
    public void Off_beat_animation_is_a_pacing_verdict_of_its_own()
    {
        var v = BottleneckClassifier.Classify(
            Result(gpuBusyMs: 7.0 * 0.95, animationErrorMsP95: 6.0));

        v.State.Should().Be(BottleneckState.FramePacing);
        v.Reason.Should().Contain("Animation error");
    }

    // ====================== la latence seule ======================

    [Fact]
    public void A_normal_latency_on_a_healthy_session_is_NOT_a_latency_verdict()
    {
        // Premier jet : la condition ne testait que « la latence a ete mesuree ». Des sessions
        // parfaitement saines ressortaient « probleme de latence ». 3 ms de latence p95 sur une
        // frame mediane de 7 ms, c'est normal, et le seuil doit etre une RELATION.
        var v = BottleneckClassifier.Classify(
            Result(gpuBusyMs: 7.0 * 0.78, gpuWaitRatio: 0.05, renderPresentLatencyMsP95: 3.0));

        v.State.Should().NotBe(BottleneckState.LatencyOnly);
        v.State.Should().Be(BottleneckState.UnknownPartial);
    }

    [Fact]
    public void A_healthy_but_laggy_session_is_a_latency_verdict()
    {
        // Cadence propre, GPU dans la bande indecise, mais la latence p95 est la. Il reste de la
        // reactivite a gagner, pas des images -- et le dire evite de vendre des FPS.
        var v = BottleneckClassifier.Classify(
            Result(gpuBusyMs: 7.0 * 0.78, gpuWaitRatio: 0.05, renderPresentLatencyMsP95: 18.0));

        v.State.Should().Be(BottleneckState.LatencyOnly);
        v.Reason.Should().Contain("not frames");
    }

    // ====================== la repetabilite ======================

    [Fact]
    public void A_single_run_is_provisional_and_never_a_strong_claim()
    {
        // Spec : « Strong claims require 3-run repeatability ».
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95, runs: 1));

        v.State.Should().Be(BottleneckState.GpuBound);
        v.Provisional.Should().BeTrue();
        v.Confidence.Should().Be(BottleneckConfidence.Medium);
        v.AllowsMeasuredGainClaim.Should().BeFalse();
    }

    [Fact]
    public void Three_runs_is_where_a_strong_claim_becomes_allowed()
    {
        var v = BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95, runs: 3));

        v.Provisional.Should().BeFalse();
        v.Confidence.Should().Be(BottleneckConfidence.High);
        v.AllowsMeasuredGainClaim.Should().BeTrue();
    }

    [Fact]
    public void Every_verdict_carries_a_reason_in_plain_words()
    {
        // Un levier supprime sans motif est un levier supprime sans recours : l'utilisateur ne
        // sait pas pourquoi, et nous ne savons pas si le classifieur s'est trompe.
        var cases = new[]
        {
            BottleneckClassifier.Classify(null),
            BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95)),
            BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.45, gpuWaitRatio: 0.40)),
            BottleneckClassifier.Classify(Result(commitPressure: 0.95)),
            BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95, spread: 1.5)),
            BottleneckClassifier.Classify(Result(gpuBusyMs: 7.0 * 0.95), gpuThrottling: true),
        };

        cases.Should().OnlyContain(v => v.Reason.Length > 40);
    }
}
