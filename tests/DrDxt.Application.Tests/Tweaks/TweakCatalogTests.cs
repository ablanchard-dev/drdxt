using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks;

// -----------------------------------------------------------------------
// TweakCatalog et la porte dure anti-liste-noire (barrière permanente).
//
// Le catalogue agrège tous les ITweak (IEnumerable<ITweak> du DI) et implémente
// l'ITweakCatalog réconcilié (Get(id) / All()).
//
//   * Porte anti-liste-noire : AllTweakIds ne contient aucun identifiant placebo interdit.
//   * Chaque levier Latency / GpuTuning / FixIt a RequiresRestorePoint == true.
//   * Le catalogue se résout via le DI.
//
// La porte anti-liste-noire est une barrière permanente : impossible d'introduire un
// levier interdit ou placebo sans faire échouer ce test.
// -----------------------------------------------------------------------
public sealed class TweakCatalogTests
{
    // Liste noire : ces identifiants placebo ou nuisibles ne doivent jamais entrer au catalogue.
    // Les entrées du domaine processeur sont les mythes démontés par la recherche processeur du projet
    // (priorité Realtime, MMCSS/SystemResponsiveness=0, C-states coupés en bloc, affinité
    // épinglée en bloc, dé-parcage en bloc, useplatformclock/HPET). Cette recherche a conclu que les
    // « leviers » powercfg fins (PROCTHROTTLEMIN/PERFBOOSTMODE) sont sans effet sur les processeurs HWP
    // et redondants avec SanePowerPlan ; plutôt que livrer des placebos, les mythes sont rendus
    // définitivement inlivrables ici (OpAllowlist les refuse déjà structurellement par son refus
    // par défaut : cette garde est la ceinture et les bretelles, et la documentation).
    // Les trois derniers ont été nommés mythes à verrouiller par la recherche du projet sur la latence
    // de l'OS, et n'avaient jamais été ajoutés ; un pack public de 74 scripts d'optimisation
    // écrit les trois (MSISupported=1 forcé par périphérique, SwapEffectUpgradeEnable=0,
    // PowerThrottlingOff). Les deux autres identifiants de cette recommandation existent déjà ici
    // sous d'autres noms : mmcss.systemresponsiveness → cpu.mmcss_disable, et
    // clock.useplatformclock → timer.useplatformclock.
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
        // Inclure deux leviers légitimes ; vérifier qu'aucun identifiant interdit ne s'y glisse.
        var catalog = new TweakCatalog(new[]
        {
            Fake("latency.reflex", TweakCategory.Latency),
            Fake("gpu.undervolt", TweakCategory.GpuTuning),
            Fake("fixit.x3d_ccd", TweakCategory.FixIt),
        });

        catalog.AllTweakIds.Should().NotContain(BannedTweakIds); // barrière dure
    }

    [Fact]
    public void A_registered_cpu_myth_tweak_is_caught_by_the_barrier()
    {
        // Prouve que la porte mord vraiment sur les mythes processeur démontés, et pas seulement
        // que la liste contient des chaînes : un catalogue qui en enregistrerait un intersecterait
        // la liste noire, donc l'assertion NotContain ci-dessus échouerait pour lui.
        var catalog = new TweakCatalog(new[]
        {
            Fake("latency.reflex", TweakCategory.Latency),
            Fake("cpu.priority_realtime", TweakCategory.FixIt), // priorité Realtime : interdite
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
        // La porte tient aussi à travers l'interface résolue.
        catalog.All().Select(t => t.Id).Should().NotContain(BannedTweakIds);
    }
}
