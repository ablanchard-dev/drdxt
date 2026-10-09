using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using DrDxt.App;
using DrDxt.App.ViewModels;
using DrDxt.App.Views;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M2 G2 — light smoke test for the before/after DashboardView.
//
// Not a pixel test. It verifies two things:
//   1. The view instantiates and its DataContext binds to the DashboardViewModel
//      (the XAML parses, the App.xaml tokens/styles resolve, no binding-time throw).
//   2. Every dashboard resx KEY the view references exists in both EN and FR, so the
//      dashboard never renders a raw key. (The headline/metric/action/tweak keys are
//      already covered by DashboardLocalizationTests; here we cover the G2 VIEW chrome
//      keys the view adds.)
//
// WPF needs an STA thread + a live Application carrying App.xaml's resources for the
// StaticResource lookups, so the view test runs inside StaApplication.Run(...).
// -----------------------------------------------------------------------
[Collection(CollectionWpf.Nom)]
public sealed class DashboardViewSmokeTests
{
    private static readonly ILocalizer Localizer = new ResxLocalizer();

    // The static label/series/section keys the DashboardView binds (beyond the keys the
    // VM already exposes and DashboardLocalizationTests covers).
    private static readonly string[] ViewChromeKeys =
    {
        "dashboard.subtitle",
        "dashboard.badge.gain",
        "dashboard.badge.no_gain",
        "dashboard.badge.reverted",
        "dashboard.chart.frametime.title",
        "dashboard.chart.frametime.caption",
        "dashboard.series.before",
        "dashboard.series.after",
        "dashboard.lows.title",
        "metric.p01_low",
        "dashboard.latency.title",
        "dashboard.unit.ms",
        "dashboard.changes.title",
        "dashboard.changes.applied",
        "dashboard.changes.reverted",
        "dashboard.changes.none",
        "tweak.latency.reflex.title",
        "tweak.latency.render_queue_cap.title",
        "tweak.latency.vrr_vsync_cap.title",
        "tweak.gpu.undervolt.title",
        "tweak.gpu.power_limit.title",
        "tweak.gpu.fan_curve.title",
        "tweak.fixit.x3d_ccd.title",
        "tweak.fixit.power_plan.title",
    };

    [Theory]
    [MemberData(nameof(KeyCases))]
    public void Every_dashboard_view_chrome_key_exists_in_fr_and_en(string key)
    {
        var en = Localizer.Get(key, new CultureInfo("en"));
        var fr = Localizer.Get(key, new CultureInfo("fr"));
        en.Should().NotBeNullOrEmpty().And.NotBe(key, $"key '{key}' missing from EN resx");
        fr.Should().NotBeNullOrEmpty().And.NotBe(key, $"key '{key}' missing from FR resx");
    }

    public static IEnumerable<object[]> KeyCases() =>
        ViewChromeKeys.Select(k => new object[] { k });

#if DEBUG
    // These two smokes load DashboardSample, the QA-only fabricated preview data that is
    // compiled OUT of Release (#if DEBUG in DrDxt.App), so they are inherently DEBUG-only —
    // guarded here so the file still compiles under -c Release.
    [Fact]
    public void View_instantiates_and_binds_its_datacontext_to_the_view_model()
    {
        var bound = StaApplication.Run(() =>
        {
            var vm = new DashboardViewModel();
            vm.Load(DashboardSample.MeasurableGain());

            var view = new DashboardView { DataContext = vm };
            view.Measure(new Size(1240, 900)); // force the template + bindings to realize

            return ReferenceEquals(view.DataContext, vm);
        });

        bound.Should().BeTrue();
    }

    [Fact]
    public void Sample_report_drives_the_gain_state_the_view_renders()
    {
        // The preview sample must be a measurable-gain report so the screenshot shows the
        // gain headline + badge and a populated changes list (honesty: still real states).
        var vm = new DashboardViewModel();
        vm.Load(DashboardSample.MeasurableGain());

        vm.HeadlineKey.Should().Be("dashboard.measurable_gain");
        vm.ShowGainBadge.Should().BeTrue();
        vm.AppliedChanges.Should().NotBeEmpty();
        vm.RevertedChanges.Should().NotBeEmpty();   // also exercises the auto-reverted list
        vm.BeforeFrameTimes.Should().NotBeEmpty();
        vm.AfterFrameTimes.Should().NotBeEmpty();
        vm.EstimatedPcLatencyAfterMs.Should().NotBeNull();
    }
#endif
}

/// <summary>
/// Runs a delegate on a dedicated STA thread under a live WPF <see cref="Application"/>
/// that loads App.xaml's resource dictionary, so views relying on StaticResource tokens
/// can be instantiated in a test. Best-effort: if WPF cannot initialize in the runner the
/// caller still has the resx-key assertions as the load-bearing coverage.
///
/// 17/09/2026 — il etait sous `#if DEBUG` au motif que son seul client etait le smoke adosse a
/// DashboardSample. Le test de contraste du chrome liquide est devenu un second client le meme
/// jour, et le projet de tests ne compilait plus du tout en Release. Le helper ne touche rien de
/// DEBUG-only : c'est la garde qui etait de trop, pas le client.
/// </summary>
internal static class StaApplication
{
    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationWithResources();
                // 29/09/2026 : le lecteur XAML de WPF partage des caches entre fils. Deux vues construites en même
                // temps sur deux fils STA levaient « clé 'RelativeSource' absente » ou « chemin 'Opacity' non
                // résolu », au hasard de la charge. L'app n'a qu'un fil d'interface ; le banc en fait autant.
                lock (Rendu) result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
            throw new InvalidOperationException("STA view smoke test failed.", captured);

        return result;
    }

    // WPF n'admet qu'une Application par AppDomain, et xUnit lance les classes de tests
    // en parallele : deux fils STA voyaient tous les deux Current == null et le second
    // levait « Impossible de creer plusieurs instances System.Windows.Application ». Le
    // verrou rend la creation atomique ; les fils suivants reutilisent l'instance vivante.
    private static readonly object Verrou = new();
    private static readonly object Rendu = new();

    private static void EnsureApplicationWithResources()
    {
        lock (Verrou)
        {
            EnsureApplicationWithResourcesCore();
        }
    }

    private static void EnsureApplicationWithResourcesCore()
    {
        if (System.Windows.Application.Current is not null)
            return;

        // Construct the App and call its generated InitializeComponent(), which loads
        // App.xaml's resource tokens (brushes/fonts) into Application.Current.Resources.
        // OnStartup is not invoked (that only runs on Application.Run), so no window / DI
        // bootstrap fires — we just get the StaticResource tokens the view binds to.
        var app = new DrDxt.App.App();
        app.InitializeComponent();
    }
}

