using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M2 G1 — DashboardViewModel.
//
// The VM consumes an ApplyReport (+ its AbVerdict) and exposes everything a
// before/after dashboard binds: before/after frametime series, 1% & 0.1% lows,
// avg FPS, the ESTIMATED PC latency (labelled "estimée", honesty §4 P1) + margin,
// a headline key, the applied vs auto-reverted tweak ids, and ShowGainBadge.
//
// Pure VM tests — no WPF. Zero hardcoded UI strings: everything is a resx key.
// -----------------------------------------------------------------------
public sealed class DashboardViewModelTests
{
    private const int Pid = 4242;

    private static BenchmarkResult Bench(
        double avg, double p1, double p01, double[] frames, double? latency, double margin) =>
        new(avg, p1, p01, frames, latency, margin, Runs: 3);

    private static readonly BenchmarkResult Before = Bench(
        avg: 200, p1: 150, p01: 120, frames: new[] { 5.0, 5.1, 4.9, 5.0 }, latency: 18, margin: 2);

    // --- Report builders for the three headline states ---

    private static ApplyReport GainReport()
    {
        var after = Bench(avg: 235, p1: 195, p01: 165, frames: new[] { 4.2, 4.3, 4.1, 4.2 }, latency: 12, margin: 2);
        var verdict = new AbVerdict(Before, after, IsMeasurableGain: true, "verdict.measurable_gain");
        var outcomes = new[]
        {
            new ApplyOutcome("latency.reflex", Applied: true, AutoReverted: false, new TweakSnapshot("latency.reflex", "{}"), Error: null),
        };
        return new ApplyReport(outcomes, verdict);
    }

    private static ApplyReport NoGainReport()
    {
        var after = Bench(avg: 201, p1: 150, p01: 120, frames: new[] { 5.0, 5.0, 5.0, 5.0 }, latency: 18, margin: 2);
        var verdict = new AbVerdict(Before, after, IsMeasurableGain: false, "verdict.no_measurable_gain");
        var outcomes = new[]
        {
            new ApplyOutcome("latency.reflex", Applied: true, AutoReverted: false, new TweakSnapshot("latency.reflex", "{}"), Error: null),
        };
        return new ApplyReport(outcomes, verdict);
    }

    private static ApplyReport RegressionRevertedReport()
    {
        var after = Bench(avg: 180, p1: 120, p01: 100, frames: new[] { 5.6, 5.5, 5.6, 5.5 }, latency: 22, margin: 2);
        var verdict = new AbVerdict(Before, after, IsMeasurableGain: false, "verdict.regression_reverted");
        var outcomes = new[]
        {
            new ApplyOutcome("gpu.undervolt", Applied: true, AutoReverted: true, new TweakSnapshot("gpu.undervolt", "{}"), Error: null),
            new ApplyOutcome("latency.reflex", Applied: true, AutoReverted: true, new TweakSnapshot("latency.reflex", "{}"), Error: null),
        };
        return new ApplyReport(outcomes, verdict);
    }

    // ====================== headline states ======================

    // 14/09/2026 (C1 bis) — le pied de page disait « Version 1.0.0 », ecrit dans les .resx, pendant
    // que l'installeur disait 0.9.0. Il porte maintenant la version de l'assembly (Directory.Build.props).
    [Fact]
    public void Le_pied_de_page_porte_la_version_de_l_assembly_pas_un_litteral()
    {
        var vm = new DashboardViewModel(safety: null, navigation: null, scanFactory: null, localize: _ => "V {0}");

        vm.LabelFooterVersion.Should().Be(
            "V " + typeof(DashboardViewModel).Assembly.GetName().Version!.ToString(3));
    }

    // 29/09/2026, critique de l'app (persona lecteur d'écran) : les quatre chiffres du tableau avant/après
    // n'avaient aucun nom ; un lecteur d'écran lisait « 198 », une flèche, « 231 », sans dire de quoi.
    [Fact]
    public void Chaque_chiffre_se_lit_en_une_phrase_pour_un_lecteur_d_ecran()
    {
        var vm = new DashboardViewModel(safety: null, navigation: null, scanFactory: null,
            localize: k => k == "Dashboard.Lecture" ? "{0} : {1} avant, {2} après" : k)
        {
            AvgFpsBefore = 198, AvgFpsAfter = 231,
            EstimatedPcLatencyBeforeMs = null, EstimatedPcLatencyAfterMs = 13,
        };

        vm.LectureFps.Should().Be("metric.avg_fps : 198 avant, 231 après");
        vm.LectureLatence.Should().Be("metric.pc_latency_estimated : Dashboard.NonMesure avant, 13 après");
        vm.LectureGpu.Should().Be("metric.gpu_busy : Dashboard.NonMesure");
    }

    // 29/09/2026, quatrième critique : « Gain mesuré au-delà de la marge d'erreur » au-dessus d'un 1 % low dont la
    // marge est inconnue. Le gain vient alors du FPS moyen ; l'écran dit que le 1 % low ne conclut pas.
    [Fact]
    public void Un_gain_dit_quand_le_1_low_n_a_pas_assez_de_passes_pour_conclure()
    {
        var vm = new DashboardViewModel(safety: null, navigation: null, scanFactory: null, localize: k => k)
        {
            ShowGainBadge = true, MarginOfErrorLow = double.PositiveInfinity,
        };
        vm.NoteDuGain.Should().Be("Dashboard.Gain.LowNonConclu");

        vm.MarginOfErrorLow = 3.2;
        vm.NoteDuGain.Should().BeNull("les deux marges sont connues");

        vm.MarginOfErrorLow = double.PositiveInfinity;
        vm.ShowGainBadge = false;
        vm.NoteDuGain.Should().BeNull("sans gain, rien à nuancer");
    }

    [Fact]
    public void Shows_no_measurable_gain_state_when_verdict_in_noise()
    {
        var vm = new DashboardViewModel();
        vm.Load(NoGainReport());

        vm.HeadlineKey.Should().Be("dashboard.no_measurable_gain");
        vm.ShowGainBadge.Should().BeFalse();
    }

    [Fact]
    public void Shows_regression_reverted_state_with_nonempty_reverted_list()
    {
        var vm = new DashboardViewModel();
        vm.Load(RegressionRevertedReport());

        vm.HeadlineKey.Should().Be("dashboard.regression_reverted");
        vm.RevertedTweakIds.Should().NotBeEmpty();
        vm.RevertedTweakIds.Should().BeEquivalentTo(new[] { "gpu.undervolt", "latency.reflex" });
        vm.ShowGainBadge.Should().BeFalse();
    }

    [Fact]
    public void Shows_measurable_gain_state_with_badge()
    {
        var vm = new DashboardViewModel();
        vm.Load(GainReport());

        vm.HeadlineKey.Should().Be("dashboard.measurable_gain");
        vm.ShowGainBadge.Should().BeTrue();
        vm.AppliedTweakIds.Should().Contain("latency.reflex");
        vm.RevertedTweakIds.Should().BeEmpty();
    }

    // ====================== honesty: latency is "estimated" ======================

    [Fact]
    public void Latency_metric_is_labelled_estimated()
    {
        var vm = new DashboardViewModel();
        vm.LatencyLabelKey.Should().Be("metric.pc_latency_estimated");
    }

    [Fact]
    public void Metric_label_keys_are_resx_keys_not_hardcoded()
    {
        var vm = new DashboardViewModel();
        vm.AvgFpsLabelKey.Should().Be("metric.avg_fps");
        vm.P1LowLabelKey.Should().Be("metric.p1_low");
        vm.RevertAllActionKey.Should().Be("action.revert_all");
    }

    // ====================== binds before/after series + lows ======================

    [Fact]
    public void Binds_before_after_framecurves_and_1pct_lows()
    {
        var vm = new DashboardViewModel();
        vm.Load(GainReport());

        vm.BeforeFrameTimes.Should().NotBeEmpty();
        vm.AfterFrameTimes.Should().NotBeEmpty();
        vm.P1LowBefore.Should().BeGreaterThan(0);
        vm.P1LowAfter.Should().BeGreaterThan(0);
        vm.P01LowBefore.Should().BeGreaterThan(0);
        vm.P01LowAfter.Should().BeGreaterThan(0);
        vm.AvgFpsBefore.Should().Be(200);
        vm.AvgFpsAfter.Should().Be(235);
    }

    [Fact]
    public void Binds_estimated_latency_before_after_and_margin()
    {
        var vm = new DashboardViewModel();
        vm.Load(GainReport());

        vm.EstimatedPcLatencyBeforeMs.Should().Be(18);
        vm.EstimatedPcLatencyAfterMs.Should().Be(12);
        // The margin comes from the after benchmark's MarginOfError.
        vm.MarginOfError.Should().Be(2);
    }

    [Fact]
    public void Binds_gpu_busy_percent_before_and_after()
    {
        var before = new BenchmarkResult(200, 150, 120, new[] { 5.0 }, 18, 2, Runs: 3, MeanGpuBusyMs: 4.0); // 4/5ms = 80%
        var after = new BenchmarkResult(250, 195, 165, new[] { 4.0 }, 12, 2, Runs: 3, MeanGpuBusyMs: 3.8);  // 3.8/4ms = 95%
        var report = new ApplyReport(
            new[] { new ApplyOutcome("latency.reflex", Applied: true, AutoReverted: false, new TweakSnapshot("latency.reflex", "{}"), Error: null) },
            new AbVerdict(before, after, IsMeasurableGain: true, "verdict.measurable_gain"));

        var vm = new DashboardViewModel();
        vm.Load(report);

        vm.GpuBusyPercentBefore.Should().BeApproximately(80.0, 1e-9);
        vm.GpuBusyPercentAfter.Should().BeApproximately(95.0, 1e-9);
    }

    [Fact]
    public void Gpu_busy_percent_null_when_not_measured()
    {
        // The default benchmarks carry no GPU-busy sample → null, never a fabricated 0.
        var vm = new DashboardViewModel();
        vm.Load(GainReport());

        vm.GpuBusyPercentBefore.Should().BeNull();
        vm.GpuBusyPercentAfter.Should().BeNull();
    }

    [Fact]
    public void Frame_series_match_the_report_benchmarks()
    {
        var vm = new DashboardViewModel();
        var report = GainReport();
        vm.Load(report);

        vm.BeforeFrameTimes.Should().Equal(report.Verdict!.Before.FrameTimesMs);
        vm.AfterFrameTimes.Should().Equal(report.Verdict!.After.FrameTimesMs);
    }

    // ====================== degraded mode (no verdict) ======================

    [Fact]
    public void No_verdict_degraded_report_shows_no_measurable_gain_and_no_badge()
    {
        // The engine kept the batch but could not re-measure (null verdict). We never
        // invent a gain: the honest headline is "no measurable gain", no badge.
        var outcomes = new[]
        {
            new ApplyOutcome("latency.reflex", Applied: true, AutoReverted: false, new TweakSnapshot("latency.reflex", "{}"), Error: null),
        };
        var vm = new DashboardViewModel();
        vm.Load(new ApplyReport(outcomes, Verdict: null));

        vm.HeadlineKey.Should().Be("dashboard.no_measurable_gain");
        vm.ShowGainBadge.Should().BeFalse();
        vm.AppliedTweakIds.Should().Contain("latency.reflex");
        vm.BeforeFrameTimes.Should().BeEmpty();   // no verdict ⇒ no series to plot
        vm.AfterFrameTimes.Should().BeEmpty();
    }

    [Fact]
    public void Applied_list_excludes_auto_reverted_ids()
    {
        var vm = new DashboardViewModel();
        vm.Load(RegressionRevertedReport());

        // In the regression case every tweak was auto-reverted, so the applied-and-kept
        // list is empty while the reverted list holds them.
        vm.AppliedTweakIds.Should().BeEmpty();
        vm.RevertedTweakIds.Should().HaveCount(2);
    }

    // 23/09/2026 — première photo de l'écran après « Optimiser » : « 198 → 231 FPS » sans marge,
    // alors que le principe n°2 du produit veut qu'un nombre voyage avec elle. MarginOfError était
    // calculée, remplie... et lue par aucune vue.
    [Fact]
    public void Le_fps_affiche_porte_sa_marge_d_erreur()
    {
        var vm = new DashboardViewModel(safety: null, navigation: null, scanFactory: null, localize: k => k + " {0}");
        vm.Load(GainReport());

        vm.MargeFpsTexte.Should().StartWith("Dashboard.Marge.Fps ");
    }

    [Fact]
    public void Le_1_pourcent_low_affiche_porte_sa_propre_marge()
    {
        var after = Bench(avg: 230, p1: 180, p01: 150, frames: new[] { 4.3 }, latency: 13, margin: 2.0) with { P1LowMarginOfError = 6.4 };
        var report = new ApplyReport(Array.Empty<ApplyOutcome>(),
            new AbVerdict(Before, after, IsMeasurableGain: false, "verdict.no_measurable_gain"));
        var vm = new DashboardViewModel(safety: null, navigation: null, scanFactory: null, localize: k => k + " {0}");
        vm.Load(report);

        vm.MargeLowTexte.Should().MatchRegex(@"^Dashboard\.Marge\.Fps 6[.,]4$", "la marge des lows, pas celle de la moyenne (2)");
        var sansMarge = new DashboardViewModel();
        sansMarge.Load(new ApplyReport(Array.Empty<ApplyOutcome>(),
            new AbVerdict(Before, after with { P1LowMarginOfError = double.PositiveInfinity }, IsMeasurableGain: false, "verdict.no_measurable_gain")));
        sansMarge.MargeLowTexte.Should().Be("Dashboard.Marge.Inconnue");
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"))
            .Should().Contain("{Binding MargeLowTexte}");
    }

    // Sans mesure du GPU (repli DXGI, vieux PresentMon), la case affichait deux tirets et un « % » : un tiret et une unité
    // qui ressemblaient à une valeur. Elle dit maintenant qu'elle n'a pas été mesurée.
    [Fact]
    public void GPU_occupe_sans_mesure_le_dit_au_lieu_d_afficher_des_tirets()
    {
        var sans = new DashboardViewModel(safety: null, navigation: null, scanFactory: null, localize: k => k);
        sans.Load(new ApplyReport(Array.Empty<ApplyOutcome>(), new AbVerdict(Before, Before, IsMeasurableGain: false, "verdict.no_measurable_gain")));
        sans.GpuBusyMesure.Should().BeFalse();
        sans.GpuNonMesureTexte.Should().Be("Dashboard.NonMesure");

        var mesure = Before with { MeanGpuBusyMs = 4.0 };
        var avec = new DashboardViewModel(safety: null, navigation: null, scanFactory: null, localize: k => k);
        avec.Load(new ApplyReport(Array.Empty<ApplyOutcome>(), new AbVerdict(mesure, mesure, IsMeasurableGain: false, "verdict.no_measurable_gain")));
        avec.GpuBusyMesure.Should().BeTrue();
        avec.GpuNonMesureTexte.Should().BeNull();

        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"))
            .Should().Contain("{Binding GpuNonMesureTexte}").And.Contain("{Binding GpuBusyMesure, Converter={StaticResource BoolVis}}");
    }

    // Le 0,1 % low n'apparaît que dans les barres, et sans marge : le chiffre le plus fragile de l'écran.
    [Fact]
    public void Le_0_1_pourcent_low_affiche_porte_sa_propre_marge()
    {
        var after = Bench(avg: 230, p1: 180, p01: 150, frames: new[] { 4.3 }, latency: 13, margin: 2.0)
            with { P1LowMarginOfError = 6.4, P01LowMarginOfError = 11.2 };
        var vm = new DashboardViewModel(safety: null, navigation: null, scanFactory: null, localize: k => k + " {0}");
        vm.Load(new ApplyReport(Array.Empty<ApplyOutcome>(),
            new AbVerdict(Before, after, IsMeasurableGain: false, "verdict.no_measurable_gain")));

        vm.MargeLow01Texte.Should().MatchRegex(@"^Dashboard\.Marge\.Fps 11[.,]2$", "la sienne, pas celle du 1 % low (6,4)");
        var sansMarge = new DashboardViewModel();
        sansMarge.Load(new ApplyReport(Array.Empty<ApplyOutcome>(),
            new AbVerdict(Before, after with { P01LowMarginOfError = double.PositiveInfinity }, IsMeasurableGain: false, "verdict.no_measurable_gain")));
        sansMarge.MargeLow01Texte.Should().Be("Dashboard.Marge.Inconnue");
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"))
            .Should().Contain("{Binding MargeLow01Texte}");
    }

    [Fact]
    public void Sans_resolution_la_marge_se_dit_inconnue_jamais_zero()
    {
        var after = Bench(avg: 230, p1: 180, p01: 150, frames: new[] { 4.3 }, latency: 13, margin: double.PositiveInfinity);
        var report = new ApplyReport(Array.Empty<ApplyOutcome>(),
            new AbVerdict(Before, after, IsMeasurableGain: false, "verdict.no_measurable_gain"));
        var vm = new DashboardViewModel();
        vm.Load(report);

        vm.MargeFpsTexte.Should().Be("Dashboard.Marge.Inconnue");
    }

    // 23/09/2026 — le graphique « Temps par image (ms) » n'avait aucune valeur d'axe : deux courbes
    // qu'on ne pouvait pas lire. Les deux courbes partagent une échelle (min en bas, max en haut) :
    // ce sont ces deux bornes qu'on écrit. Valeurs de l'échantillon, à la main : max 5,1, min 4,1.
    [Fact]
    public void Le_graphique_dit_son_echelle()
    {
        var avant = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var vm = new DashboardViewModel();
            vm.Load(GainReport());

            vm.EchelleHautTexte.Should().Be("5,1 ms");
            vm.EchelleBasTexte.Should().Be("4,1 ms");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = avant; }
    }

    // 29/09/2026, critique (persona lecteur d'écran) : le graphique n'avait aucun équivalent texte. Il se lit
    // en une phrase : la médiane des temps par image avant, puis après.
    [Fact]
    public void Le_graphique_se_lit_en_une_phrase()
    {
        var vm = new DashboardViewModel(safety: null, navigation: null, scanFactory: null,
            localize: k => k == "Dashboard.Lecture.Graphique" ? "médiane {0:0.0} puis {1:0.0}" : k);
        vm.Load(GainReport());

        var avant = GainReport().Verdict!.Before.FrameTimesMs.OrderBy(x => x).ToList();
        var apres = GainReport().Verdict!.After.FrameTimesMs.OrderBy(x => x).ToList();
        vm.LectureGraphique.Should().Be(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            "médiane {0:0.0} puis {1:0.0}", avant[avant.Count / 2], apres[apres.Count / 2]));
    }

    // 29/09/2026, critique (joueur expert) : 700 ms de maintien sur chaque petite action, la signature devenait une
    // taxe. Une mesure (rien n'est modifié) se lance en 350 ms ; ce qui touche le système garde les 700 ms.
    [Theory]
    [InlineData("RemesurerProcesseurCommand", true)]
    [InlineData("RemesurerMemoireCommand", true)]
    [InlineData("LancerTestMemoireCommand", true)]
    [InlineData("RendreBcdCommand", false)]
    [InlineData("LancerReparationCommand", false)]
    public void Une_mesure_se_maintient_moins_longtemps_qu_un_changement_du_systeme(string commande, bool mesure)
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var fin = xaml.IndexOf("{Binding " + commande + "}", StringComparison.Ordinal);
        var debut = xaml.LastIndexOf("<effets:NoyauDeMaintien", fin, StringComparison.Ordinal);
        var balise = xaml[debut..fin];

        if (mesure) balise.Should().Contain("DureeMaintien=\"0:0:0.35\"");
        else balise.Should().NotContain("DureeMaintien", "un changement du système garde le maintien complet");
    }

    [Fact]
    public void La_vue_affiche_l_echelle_du_graphique()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"));
        xaml.Should().Contain("{Binding EchelleHautTexte}").And.Contain("{Binding EchelleBasTexte}");
    }

    [Fact]
    public void La_vue_affiche_la_marge()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"))
            .Should().Contain("{Binding MargeFpsTexte}", "une propriété remplie que personne n'affiche ne dit rien");
    }
}
