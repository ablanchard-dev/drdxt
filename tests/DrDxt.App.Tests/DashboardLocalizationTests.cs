using System.Globalization;
using System.Resources;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Intel;
using DrDxt.Detection.Network;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M2 G3 — i18n completeness for the before/after dashboard.
//
// Every dashboard key the DashboardViewModel binds — AND every SummaryKey /
// guide key the D/E/F tweaks emit — must exist in both the EN and FR resx, so no
// tweak ever renders a raw key. The key-sets must be identical (parity), mirroring
// the ScanLocalizationTests pattern. ResxLocalizer returns the key itself when a
// key is missing, so a real value must differ from the key.
// -----------------------------------------------------------------------
public sealed class DashboardLocalizationTests
{
    private static readonly ILocalizer Localizer = new ResxLocalizer();

    // ATTENTION — `ILocalizer.Get(key, fr)` passe par ResourceManager, qui RETOMBE sur la culture
    // neutre : une clé absente du resx FR rend tranquillement la phrase anglaise. Vérifier
    // `valeur != clé` ne prouve donc rien sur le français. (Constaté par mutation le 21/08/2026 :
    // traduction FR retirée, les 125 tests restaient verts.)
    // Le seul contrôle étanche est de lire le jeu de ressources sans remontée aux parents.
    private static readonly ResourceManager Resources =
        new("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);

    private static bool ExistsIn(string culture, string key)
    {
        var set = Resources.GetResourceSet(new CultureInfo(culture), createIfNotExists: true, tryParents: false);
        return !string.IsNullOrEmpty(set?.GetString(key));
    }

    [Theory]
    // ── Dashboard headline / verdict states ──
    [InlineData("dashboard.measurable_gain")]
    [InlineData("dashboard.no_measurable_gain")]
    [InlineData("dashboard.regression_reverted")]
    // ── Metrics (honesty §4 P1: latency is "estimated") ──
    [InlineData("metric.pc_latency_estimated")]
    [InlineData("metric.p1_low")]
    [InlineData("metric.avg_fps")]
    // ── Actions ──
    [InlineData("action.revert_all")]
    // ── Engine verdict SummaryKeys (emitted by TweakEngine) ──
    [InlineData("verdict.measurable_gain")]
    [InlineData("verdict.no_measurable_gain")]
    [InlineData("verdict.regression_reverted")]
    // ── Every tweak SummaryKey (D/E/F) so no tweak renders a raw key ──
    [InlineData("tweak.latency.reflex.driver_floor")]
    [InlineData("tweak.latency.render_queue_cap.floor")]
    [InlineData("tweak.latency.vrr_vsync_cap.combo")]
    [InlineData("tweak.gpu.undervolt.efficiency")]
    [InlineData("tweak.gpu.power_limit.efficiency")]
    [InlineData("tweak.gpu.fan_curve.cooler")]
    [InlineData("tweak.fixit.x3d_ccd.repair_routing")]
    [InlineData("tweak.cpu.power_policy.raise_perf_preference")]
    // ── Guide key the F2 stale-driver tweak raises ──
    public void Dashboard_key_exists_in_fr_and_en(string key)
    {
        // Présence réelle dans chaque resx (sans remontée) : `Get(key, "fr")` retomberait sur
        // l'anglais et ferait passer une clé française absente.
        ExistsIn("en", key).Should().BeTrue($"la clé '{key}' manque au resx EN");
        ExistsIn("fr", key).Should().BeTrue($"la clé '{key}' manque au resx FR");
    }

    // -------------------------------------------------------------------
    // La liste ci-dessus est écrite à la main, donc elle ne prouve que ce que quelqu'un a
    // pensé à y ajouter. Constaté le 21/08/2026 : cinq SummaryKeys (le lot NS-1 — HAGS, Game
    // Mode, Game DVR, VRR/flip-model, Prefer Max Perf) n'y étaient pas. Leurs entrées resx
    // existaient bien, donc rien n'avait cassé — mais par chance, pas par méthode : le test
    // NOMMAIT la garantie « aucun tweak ne rend une clé brute » sans la prouver.
    //
    // Les deux tests suivants la dérivent de la réalité : le catalogue réel des tweaks, et la
    // sortie réelle de l'advisor Intel. Un nouveau levier sans traduction casse la build.
    // -------------------------------------------------------------------

    [Fact]
    public void Every_REGISTERED_tweak_summary_key_resolves_in_both_languages()
    {
        using var provider = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
        var tweaks = provider.GetServices<ITweak>().ToList();
        tweaks.Should().NotBeEmpty("sinon ce test passerait sur un catalogue vide");

        var missing = (from t in tweaks
                       from culture in new[] { "en", "fr" }
                       where !ExistsIn(culture, t.SummaryKey)
                       select $"{t.Id} -> {t.SummaryKey} [{culture}]").ToList();

        missing.Should().BeEmpty("chaque tweak enregistré doit avoir sa phrase en EN et en FR");
    }

    [Fact]
    public void Every_intel_finding_key_resolves_in_both_languages()
    {
        // Les findings Intel sont produits par un advisor pur : on l'interroge avec une Arc de
        // synthèse plutôt que de recopier ses clés à la main (recopier, c'est retomber dans le
        // défaut que ce bloc corrige).
        var arc = new GpuInfo("Intel(R) Arc(TM) A770 Graphics", "Intel Corporation", 16L << 30,
                              "31.0.101.5522", TriState.Off);

        var advice = IntelGpuAdvisor.Advise(new[] { arc });
        advice.Should().NotBeEmpty();

        var missing = (from g in advice
                       from culture in new[] { "en", "fr" }
                       where !ExistsIn(culture, g.MessageKey)
                       select $"{g.Id} -> {g.MessageKey} [{culture}]").ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Every_network_finding_key_resolves_in_both_languages()
    {
        // Même dérivation que pour Intel : on interroge l'advisor plutôt que de recopier ses
        // clés. Une carte avec RSS coupé et la modération active déclenche les deux conseils.
        var nic = new NetworkAdapterState("Ethernet", "Realtek Gaming 2.5GbE", IsWired: true,
                                          InterruptModeration: TriState.On, Rss: TriState.Off,
                                          EnergyEfficientEthernet: TriState.Off);

        var advice = NetworkAdvisor.Advise(new[] { nic });
        advice.Should().HaveCount(2);

        var missing = (from g in advice
                       from culture in new[] { "en", "fr" }
                       where !ExistsIn(culture, g.MessageKey)
                       select $"{g.Id} -> {g.MessageKey} [{culture}]").ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Latency_label_key_is_honestly_estimated()
    {
        // The latency metric must never be sold as a precise figure: its label key is
        // explicitly "estimated" (honesty §4 P1).
        var en = Localizer.Get("metric.pc_latency_estimated", new CultureInfo("en"));
        var fr = Localizer.Get("metric.pc_latency_estimated", new CultureInfo("fr"));
        en.ToLowerInvariant().Should().Contain("estimat");
        fr.ToLowerInvariant().Should().Contain("estim");
    }
}
