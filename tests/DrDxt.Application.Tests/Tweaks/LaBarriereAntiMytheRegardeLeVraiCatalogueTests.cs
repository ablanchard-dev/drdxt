using DrDxt.Application.DependencyInjection;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Application.Tests.Tweaks;

// ─────────────────────────────────────────────────────────────────────────────
// 17/09/2026 — la barriere anti-mythe ne regardait pas le catalogue livre.
//
// Le README annonce, parmi les cinq invariants : « Les mythes (Nagle/TcpAckFrequency,
// NetworkThrottlingIndex, debloat de services, polling 8000 Hz, timer global, priorite
// Realtime, C-states off en bloc…) sont rendus *inshippables* par un test qui casse si
// l'un d'eux entre au catalogue. » L'en-tete de `TweakCatalogTests` va plus loin : « it is
// impossible to introduce a banned/placebo tweak without failing this test ».
//
// C'etait faux. Les quatre tests de cette barriere construisent chacun leur propre
// catalogue, a partir de deux ou trois `Substitute.For<ITweak>()` fabriques sur place, puis
// verifient que ce catalogue-la ne contient aucun mythe. Y compris celui qui passe par le
// conteneur : il enregistre des doublures, pas les vrais leviers. Le catalogue reellement
// livre — celui que `AddDrDxtApplication()` compose — n'etait regarde par aucun d'eux.
//
// Autrement dit : ajouter `cpu.priority_realtime` au produit demain laissait la suite verte,
// et la phrase du README continuait de promettre le contraire. C'est le mode de panne que ce
// depot traque partout ailleurs : un test qui nomme une garantie sans la prouver.
//
// Ce fichier regarde le vrai catalogue, et rien d'autre.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class LaBarriereAntiMytheRegardeLeVraiCatalogueTests
{
    /// <summary>
    /// La liste du §6, recopiee ici EXPRES plutot que partagee avec `TweakCatalogTests` :
    /// deux gardes qui puisent dans la meme liste tombent ensemble si quelqu'un allege la
    /// liste. Ici la liste est le CONTRAT, et un mythe retire d'un cote doit rougir de l'autre.
    /// </summary>
    private static readonly string[] MythesInshippables =
    {
        "net.nagle", "net.tcpack", "net.throttlingindex", "debloat.services",
        "polling.8000hz", "timer.global", "rebar.force_global", "ecores.disable_default",
        "cpu.priority_realtime", "cpu.mmcss_disable", "cpu.cstates_disable_all",
        "cpu.affinity_pin_blanket", "cpu.unpark_all", "timer.useplatformclock",
        "msi.force", "fso.disable_global", "powerthrottling.global_off",
    };

    private static ServiceProvider VraiConteneur()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticsLog>(new JournalMuet());
        services.AddSingleton<IBenchmarkRunner>(new BancMuet());
        services.AddDrDxtApplication();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Le_catalogue_LIVRE_ne_contient_aucun_mythe()
    {
        using var provider = VraiConteneur();
        var catalogue = provider.GetRequiredService<ITweakCatalog>();

        var ids = catalogue.All().Select(t => t.Id).ToArray();

        ids.Should().NotBeEmpty(
            "un catalogue vide ferait passer cette barriere sans rien garder");
        ids.Should().NotIntersectWith(MythesInshippables);
    }

    [Fact]
    public void La_barriere_tombe_si_un_mythe_entre_au_catalogue_LIVRE()
    {
        // Une barriere qu'on ne peut pas faire tomber sur commande ne prouve rien : elle
        // dirait surtout que le produit n'a pas de levier nomme comme ca. On ajoute donc un
        // mythe au conteneur reel — un ITweak de plus, exactement comme le ferait un
        // enregistrement de trop dans `AddDrDxtApplication` — et l'assertion ci-dessus doit
        // passer de vraie a fausse sur le meme catalogue.
        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticsLog>(new JournalMuet());
        services.AddSingleton<IBenchmarkRunner>(new BancMuet());
        services.AddDrDxtApplication();
        services.AddSingleton<ITweak>(new MytheRealtime());

        using var provider = services.BuildServiceProvider();
        var ids = provider.GetRequiredService<ITweakCatalog>().All().Select(t => t.Id).ToArray();

        ids.Should().Contain("cpu.priority_realtime");
        ids.Should().IntersectWith(MythesInshippables,
            "c'est l'assertion du test precedent, et elle doit se retourner ici");
    }

    /// <summary>La priorite Realtime : le mythe le mieux documente de `cpu-all.md` §3.</summary>
    private sealed class MytheRealtime : ITweak
    {
        public string Id => "cpu.priority_realtime";
        public TweakCategory Category => TweakCategory.FixIt;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "tweak.myth.realtime";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() =>
            Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "mythe"));
        public Task<Result> RevertAsync(TweakSnapshot snapshot) => Task.FromResult(Result.Ok());
    }

    private sealed class JournalMuet : IDiagnosticsLog
    {
        public void Change(string tweakId, string from, string to, string actor) { }
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    private sealed class BancMuet : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(
            int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            Task.FromResult(Result<BenchmarkResult>.Fail(ErrorCodes.BenchmarkFailed, "muet"));
    }
}
