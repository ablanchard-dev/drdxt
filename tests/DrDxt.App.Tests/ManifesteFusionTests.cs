using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using DrDxt.TestSupport;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le manifeste signe etait remplace a chaque clic.
///
/// Le vrai chemin de l'App : deux clics sur « Optimiser », un vrai magasin signe (cle de test, stockage
/// en memoire). Chaque clic arme une correction au redemarrage differente. Avant la fusion, le second
/// commit ecrasait le premier : la desinstallation, qui ne lit que ce manifeste, ne rendait plus la
/// premiere correction.
/// </summary>
public sealed class ManifesteFusionTests
{
    internal sealed class StockageEnMemoire : IManifestStorage
    {
        public SignedManifestEnvelope? Slot { get; set; }

        public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
        {
            Slot = envelope;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
            => Task.FromResult(Result<SignedManifestEnvelope?>.Ok(Slot));
    }

    internal static SignedDesiredStateStore MagasinSigne(IManifestStorage stockage) => new(
        stockage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
        ManifestDevKeys.CreateVerifier().Value!, new DefaultReconcilabilityPolicy(), new SystemClock());

    private static ApplyOutcome Garde(string id, string origine)
        => new(id, Applied: true, AutoReverted: false, new TweakSnapshot(id, origine), Error: null);

    private static ApplyReport Rapport(params ApplyOutcome[] o)
    {
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        return new ApplyReport(o.ToList(), new AbVerdict(bl, bl, false, "verdict.no_measurable_gain"));
    }

    private static ScanViewModel Vm(ITweakEngine moteur, IDesiredStateStore magasin)
    {
        var hw = new HardwareInventory(
            new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
            new[] { new GpuInfo("NVIDIA RTX 4070", "NVIDIA", 12L << 30, "551", TriState.On) },
            new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
            new MotherboardInfo("MSI", "B650"), new BiosInfo("1", DateTimeOffset.UtcNow, true), ChassisType.Desktop);
        var st = new SettingsState(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(hw));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(hw, st,
              new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) }, bl)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));
        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(Result.Ok());

        var consumer = ScanViewModelTestSeams.ProfileConsumer(
            ScanViewModelTestSeams.PlanConsumption(new[] { "latency.hagsOn" }));
        return new ScanViewModel(
            hd, sr, br, sc, sd, safety, moteur, consumer, magasin, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);
    }

    [Fact]
    public async Task Deux_clics_deux_corrections_au_redemarrage_le_manifeste_garde_LES_DEUX()
    {
        var magasin = MagasinSigne(new StockageEnMemoire());
        var moteur = Substitute.For<ITweakEngine>();
        moteur.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Result<ApplyReport>.Ok(Rapport(Garde("fixit.power_plan", "\"balanced\""))),
                       Result<ApplyReport>.Ok(Rapport(Garde("latency.reflex", "\"0\""))));
        var vm = Vm(moteur, magasin);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        // La premisse : les deux clics ont commite, aucun n'a echoue.
        await moteur.ReceivedWithAnyArgs(2).ApplyAsync(default!, default, default, default);
        vm.MoatUnarmedMessage.Should().BeNullOrEmpty();

        var lu = await magasin.LoadAsync();
        lu.IsOk.Should().BeTrue();
        lu.Value!.Tweaks.Select(t => (t.TweakId, t.OriginalValueJson)).Should().BeEquivalentTo(new[]
        {
            ("fixit.sanePowerPlan", "\"balanced\""),
            ("latency.reflex.lowLatencyMode", "\"0\""),
        }, "la correction armee au 1er clic doit rester rendable par la desinstallation apres le 2e");
    }

    // ── Un levier garde en session, rendu par une desinstallation simulee ──────────────────────────
    //
    // Le vrai chemin : GameOptimizationSession + TweakCandidate du produit (journal reel en dossier
    // temporaire, manifeste signe reel), puis RenduALaDesinstallation,
    // le chemin du helper a la desinstallation. Seul le canal eleve est une doublure : il applique l'op a une « machine ».

    private const string Nvtweak = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const uint Origine = 1, Cible = 2;

    private sealed class Machine { public uint Valeur = Origine; }

    private static string Op(uint v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(
        RegistryHiveKind.LocalMachine, Nvtweak, "LowLatencyMode", RegistryValueType.DWord,
        v.ToString(System.Globalization.CultureInfo.InvariantCulture))).Value!;

    private sealed class Levier(Machine m) : ITweak
    {
        public string Id => "latency.reflex";
        public TweakCategory Category => TweakCategory.Latency;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "tweak.latency.reflex";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;

        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            var s = new TweakSnapshot(Id, Op(m.Valeur), Op(Cible));
            m.Valeur = Cible;
            return Task.FromResult(Result<TweakSnapshot>.Ok(s));
        }

        public Task<Result> RevertAsync(TweakSnapshot snapshot)
        {
            m.Valeur = uint.Parse(((SetRegistryValueOp)PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson).Value!).Value,
                System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class Registre(Machine m) : DrDxt.Application.Tweaks.Hardware.IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) => Result<uint?>.Ok(m.Valeur);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
    }

    /// <summary>Le levier ameliore le p95 : le moteur le garde.</summary>
    private sealed class Banc(Machine m) : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
            => Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(
                100, 60, 48, new List<double> { 10.0 }, null, 1.0, runs,
                P95FrametimeMs: m.Valeur == Cible ? 11.0 : 12.0, HygieneKeptFraction: 1.0)));
    }

    /// <summary>Le canal eleve : il execute l'op typee que le SafetyManager decode du manifeste.</summary>
    private sealed class CanalEleve(Machine m) : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is not SetRegistryValueOp { Name: "LowLatencyMode" } s)
                return Task.FromResult(Result.Fail("X", $"op inattendue : {op.GetType().Name}"));
            m.Valeur = uint.Parse(s.Value, System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class Lecture(Machine m) : DrDxt.Reconciler.Desinstallation.ILectureDesCibles
    {
        public Result<string?> Lire(PrivilegedOp ecriture)
            => Result<string?>.Ok(m.Valeur.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public Result<bool> CibleExiste(PrivilegedOp ecriture) => Result<bool>.Ok(true);
    }

    [Fact]
    public async Task Un_levier_GARDE_en_session_est_rendu_a_son_origine_par_une_desinstallation_simulee()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-desinstall-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var m = new Machine();
            var magasin = MagasinSigne(new StockageEnMemoire());
            var candidat = new DrDxt.Application.Autopilot.TweakCandidate(new Levier(m),
                new CandidateDescriptor("latency.reflex", CandidateKind.AutoHot, 1, false, new[] { "latence" }),
                new Registre(m),
                new DrDxt.Application.Autopilot.FichierRevertEnAttente(System.IO.Path.Combine(dossier, "pending-revert.json")),
                manifeste: magasin);

            var profil = new DrDxt.Application.Autopilot.GameOptimizationSession(new Banc(m),
                    () => new DrDxt.Application.Autopilot.GameProfileIdentity("Warzone.exe", null, null, null, null))
                .Run(1234, new IAutopilotCandidate[] { candidat });

            // La premisse : garde, pose, et inscrit.
            profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);
            m.Valeur.Should().Be(Cible);
            candidat.OrigineHorsManifeste.Should().BeNull();

            // Le rendu passe par le vrai chemin de la desinstallation (le helper, --uninstall-revert).
            var rapport = await new DrDxt.Reconciler.Desinstallation.RenduALaDesinstallation(magasin, new Lecture(m), new CanalEleve(m))
                .ExecuterAsync(CancellationToken.None);

            rapport.ManifesteNonLu.Should().BeNull();
            rapport.Lignes.Should().ContainSingle();
            m.Valeur.Should().Be(Origine, "la desinstallation rend le levier garde a l'etat d'avant le premier changement");
        }
        finally
        {
            try { System.IO.Directory.Delete(dossier, recursive: true); } catch { /* dossier temporaire */ }
        }
    }
}
