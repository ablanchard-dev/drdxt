using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// L'autopilote et l'essai en alternance ne gardent ou n'essaient qu'une pose relue « en place » (AppliedStateVerifier).
/// Un levier qu'ils proposent mais dont la pose ne se relit pas serait rendu à chaque clic, ou jamais essayé, sans un mot.
/// Chaque levier proposable du VRAI catalogue est donc posé sur une machine simulée (ce que le helper écrit, les lecteurs le
/// relisent), et sa pose doit se relire Applied. Doublures seulement : rien ne lit ni n'écrit la vraie machine.
/// </summary>
public sealed class ChaqueLevierProposableSeRelitTests
{
    private const string Jeu = @"C:\Games\cod.exe";
    private const string Base = "Base Profile";
    private const ulong Unite = 0x3, Registre65W = (1752UL << 32) | (1UL << 47) | (1UL << 15) | 520UL, Info65W = 520UL;

    /// <summary>Ce que la carte et le processeur portent : le faux helper l'écrit, les lecteurs le relisent.</summary>
    private sealed class Machine : IElevatedClient
    {
        public Dictionary<string, string> Registre { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(uint, string), uint> Drs { get; } = new();
        public Guid Plan { get; set; } = KnownPowerPlans.Balanced;
        public Dictionary<Guid, uint> Boost { get; } = new();
        public Dictionary<string, ServiceState> Services { get; } = new(StringComparer.OrdinalIgnoreCase) { ["AMD3DVCacheSvc"] = ServiceState.Stopped };
        public int PuissanceNvidia { get; set; } = 100;
        public Dictionary<AdlxTuningKind, int> Adlx { get; } = new()
        {
            [AdlxTuningKind.PowerLimit] = 100, [AdlxTuningKind.AntiLag] = 0, [AdlxTuningKind.VerticalRefresh] = 1,
            [AdlxTuningKind.FrameRateTarget] = -60, [AdlxTuningKind.Undervolt] = 2900,
        };
        public ulong Pl { get; set; } = Registre65W;

        public static string Cle(RegistryHiveKind h, string p, string n) => $"{h}|{p.Trim('\\')}|{n}";

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            switch (op)
            {
                case SetRegistryValueOp r: Registre[Cle(r.Hive, r.Path, r.Name)] = r.Value; break;
                case DeleteRegistryValueOp d: Registre.Remove(Cle(d.Hive, d.Path, d.Name)); break;
                case ApplyNvApiDrsSettingOp a: Drs[(a.SettingId, a.ProfileName)] = a.Value; break;
                case DeleteNvApiDrsSettingOp x: Drs.Remove((x.SettingId, x.ProfileName)); break;
                case SetActivePowerPlanOp p: Plan = p.PlanGuid; break;
                case SetProcessorBoostModeOp b: Boost[b.PlanGuid] = b.AcValue; break;
                case RunServiceControlOp { Action: ServiceAction.Start } s: Services[s.ServiceName] = ServiceState.Running; break;
                case RunServiceControlOp { Action: ServiceAction.Stop } s: Services[s.ServiceName] = ServiceState.Stopped; break;
                case ApplyNvApiPowerLimitOp n: PuissanceNvidia = n.PowerLimitPercent; break;
                case ApplyAdlxTuningOp a: Adlx[a.Kind] = a.IntValue; break;
                case SetCpuPackagePowerLimitOp c: Pl = c.Valeur; break;
            }
            return Task.FromResult(Result.Ok());
        }

        public uint DrsGlobal(uint id) => Drs.TryGetValue((id, Base), out var v) ? v : 0;
    }

    private sealed record Banc(ITweakCatalog Catalogue, IRegistryReader Registre, INvApi NvApi, IPowerPlanReader Alimentation,
        IServiceStateReader Services, AppliedStateVerifier.LecteursCarte Carte);

    private static Banc Monter(Machine m, bool nvidia)
    {
        static Result<T> Ok<T>(T v) => Result<T>.Ok(v);

        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(default, default!, default!).ReturnsForAnyArgs(c =>
            Ok<uint?>(m.Registre.TryGetValue(Machine.Cle(c.ArgAt<RegistryHiveKind>(0), c.ArgAt<string>(1), c.ArgAt<string>(2)), out var v) ? uint.Parse(v) : null));
        registre.ReadString(default, default!, default!).ReturnsForAnyArgs(c =>
            Ok(m.Registre.TryGetValue(Machine.Cle(c.ArgAt<RegistryHiveKind>(0), c.ArgAt<string>(1), c.ArgAt<string>(2)), out var v) ? v : null));
        registre.ReadMultiString(default, default!, default!).ReturnsForAnyArgs(c =>
            Ok(m.Registre.TryGetValue(Machine.Cle(c.ArgAt<RegistryHiveKind>(0), c.ArgAt<string>(1), c.ArgAt<string>(2)), out var v) ? v : null));

        var nv = Substitute.For<INvApi>();
        nv.IsAvailable.Returns(nvidia);
        nv.GetMaxPreRenderedFrames().Returns(_ => Ok(m.DrsGlobal(0x007BA09E)));
        nv.GetPreferredPstate().Returns(_ => Ok(m.DrsGlobal(0x1057EB71)));
        nv.GetVsyncMode().Returns(_ => Ok(m.DrsGlobal(0x00A879CF)));
        nv.GetFrameRateLimit().Returns(_ => Ok(m.DrsGlobal(0x10835002)));
        nv.GetLowLatencyMode().Returns(_ => Ok(0u));
        nv.GetFrameRateLimitOrigin().Returns(Ok(new DrsOriginState(DrsValueOrigin.DriverDefault, false, "test")));
        nv.GetDrsForApplication(default, default!).ReturnsForAnyArgs(c => m.Drs.TryGetValue((c.ArgAt<uint>(0), c.ArgAt<string>(1)), out var v)
            ? Ok((v, new DrsOriginState(DrsValueOrigin.SetByUser, true, "test")))
            : Ok((m.DrsGlobal(c.ArgAt<uint>(0)), new DrsOriginState(DrsValueOrigin.InheritedFromBaseProfile, false, "test"))));

        var adlx = Substitute.For<IAdlx>();
        adlx.IsAvailable.Returns(!nvidia);
        adlx.GetAntiLagState().Returns(_ => Ok(m.Adlx[AdlxTuningKind.AntiLag]));
        adlx.GetVsyncMode().Returns(_ => Ok(m.Adlx[AdlxTuningKind.VerticalRefresh]));
        adlx.GetFrameRateTarget().Returns(_ => Ok(m.Adlx[AdlxTuningKind.FrameRateTarget]));
        adlx.GetChillActif().Returns(Ok(false));
        adlx.GetEnhancedSyncActif().Returns(Ok(false));

        var nvCarte = Substitute.For<INvApiGpuTuning>();
        nvCarte.IsAvailable.Returns(nvidia);
        nvCarte.GetPowerLimitPct(0).Returns(_ => Ok(m.PuissanceNvidia));
        var amdCarte = Substitute.For<IAdlxGpuTuning>();
        amdCarte.IsAvailable.Returns(!nvidia);
        amdCarte.ResolveTunableGpuIndex().Returns(Ok(0));
        amdCarte.GetPowerLimitPct(0).Returns(_ => Ok(m.Adlx[AdlxTuningKind.PowerLimit]));
        amdCarte.GetGfxMaxFreqMhz(0).Returns(_ => Ok(m.Adlx[AdlxTuningKind.Undervolt]));

        var alimentation = Substitute.For<IPowerPlanReader>();
        alimentation.GetActivePowerPlan().Returns(_ => Ok(m.Plan));
        alimentation.LireModeDeBoost(default).ReturnsForAnyArgs(c => Ok(new ModeDeBoostLu(c.Arg<Guid>(),
            m.Boost.TryGetValue(c.Arg<Guid>(), out var b) ? b : 0, 2)));
        var cpu = Substitute.For<ICpuPowerLimitReader>();
        cpu.LireRegistres().Returns(_ => Ok(new LectureLimiteCpu(new RegistresCpuLus(Unite, m.Pl, Info65W),
            RegistreDeLimiteCpu.Decoder(Unite, m.Pl, Info65W)! with { CopieMmioPl1W = 0, ChaleurEnPartieMaxC = 80 })));
        var services = Substitute.For<IServiceStateReader>();
        services.GetServiceState(default!).ReturnsForAnyArgs(c => Ok(m.Services.TryGetValue(c.Arg<string>(), out var s) ? s : ServiceState.NotInstalled));

        var jeu = Substitute.For<IGameExecutable>();
        jeu.RunningGameExecutablePath().Returns(Jeu);
        jeu.ModulesDuJeu().Returns(["d3d11.dll"]);
        var ecran = Substitute.For<IDisplayInfo>();
        ecran.GetPrimaryGameDisplay().Returns(Ok(new DisplayInfo(true, true, 144, 144, VrrPossible: TriState.On, VrrEnabled: TriState.On)));

        var sc = new ServiceCollection();
        sc.AddSingleton(Substitute.For<IDiagnosticsLog>());
        sc.AddSingleton(Substitute.For<IBenchmarkRunner>());
        sc.AddSingleton<IElevatedClient>(m);
        sc.AddSingleton(registre);
        sc.AddSingleton(nv);
        sc.AddSingleton(adlx);
        sc.AddSingleton(nvCarte);
        sc.AddSingleton(amdCarte);
        sc.AddSingleton(alimentation);
        sc.AddSingleton(cpu);
        sc.AddSingleton(services);
        sc.AddSingleton(jeu);
        sc.AddSingleton(ecran);
        sc.AddDrDxtApplication();
        return new Banc(sc.BuildServiceProvider().GetRequiredService<ITweakCatalog>(), registre, nv, alimentation, services,
            new AppliedStateVerifier.LecteursCarte(nvCarte, amdCarte, adlx, cpu));
    }

    /// <summary>Les leviers que l'autopilote et l'essai ne proposent jamais, et pourquoi.</summary>
    private static readonly Dictionary<string, string> JamaisProposes = new(StringComparer.Ordinal)
    {
        ["gpu.core_offset"] = "réglage de la carte : CandidateSelector.ReglagesCarteInterdits",
        ["gpu.undervolt"] = "réglage de la carte : CandidateSelector.ReglagesCarteInterdits",
        ["gpu.fan_curve"] = "jamais applicable (LeviersJamaisApplicablesTests) ; le ventilateur s'écrit en mode Expert",
    };

    /// <summary>Chaque levier proposable, sur la carte qui l'ouvre ; ceux qui ont deux voies, sur les deux.</summary>
    public static TheoryData<string, bool> Proposables() => new()
    {
        { "gpu.power_limit", true },
        { "latency.render_queue_cap", true }, { "latency.render_queue_cap", false },
        { "latency.vrr_vsync_cap", true }, { "latency.vrr_vsync_cap", false },
        { "latency.prefer_max_perf", true }, { "latency.reflex", true },
        { "fixit.power_plan", true }, { "fixit.x3d_ccd", true }, { "fixit.mode_boost", true }, { "fixit.cpu_power_limit", true },
    };

    [Theory]
    [MemberData(nameof(Proposables))]
    public async Task La_pose_d_un_levier_proposable_se_relit_en_place(string id, bool nvidia)
    {
        var banc = Monter(new Machine(), nvidia);
        var levier = banc.Catalogue.Get(id).Value!;

        var pose = await levier.ApplyAsync();

        pose.IsOk.Should().BeTrue(pose.Error?.Message);
        var preuve = AppliedStateVerifier.Verifier(pose.Value, banc.Registre, banc.NvApi, banc.Alimentation, banc.Services, banc.Carte);
        preuve.Verdict.Should().Be(EtatApplique.Applied,
            $"{id} ({(nvidia ? "NVIDIA" : "AMD")}) : {preuve.Cible} origine {preuve.OriginalState}, demandé {preuve.RequestedState}, lu {preuve.ObservedState}");
    }

    // « Annulé » ne se dit que relu à l'origine : l'annulation de chaque levier proposable doit donc se relire NotApplied.
    [Theory]
    [MemberData(nameof(Proposables))]
    public async Task L_annulation_d_un_levier_proposable_se_relit_a_l_origine(string id, bool nvidia)
    {
        var banc = Monter(new Machine(), nvidia);
        var levier = banc.Catalogue.Get(id).Value!;
        var pose = await levier.ApplyAsync();
        pose.IsOk.Should().BeTrue(pose.Error?.Message);

        var annule = await levier.RevertAsync(pose.Value!);

        annule.IsOk.Should().BeTrue(annule.Error?.Message);
        var preuve = AppliedStateVerifier.Verifier(pose.Value, banc.Registre, banc.NvApi, banc.Alimentation, banc.Services, banc.Carte);
        preuve.Verdict.Should().Be(EtatApplique.NotApplied,
            $"{id} ({(nvidia ? "NVIDIA" : "AMD")}) : {preuve.Cible} origine {preuve.OriginalState}, demandé {preuve.RequestedState}, lu {preuve.ObservedState}");
    }

    // Un levier à chaud ajouté au catalogue entre ici, ou dans les exceptions avec sa raison.
    [Fact]
    public void Chaque_levier_a_chaud_est_couvert_ou_dit_jamais_propose()
    {
        var couverts = Proposables().Select(r => (string)r[0]).ToHashSet();
        CatalogueDeCandidats.Ids.Where(id => CatalogueDeCandidats.Decrire(id) is { Kind: CandidateKind.AutoHot })
            .Should().BeEquivalentTo(couverts.Concat(JamaisProposes.Keys));
        JamaisProposes.Keys.Where(id => id != "gpu.fan_curve").Should().OnlyContain(id => CandidateSelector.ReglagesCarteInterdits.Contains(id));
    }
}
